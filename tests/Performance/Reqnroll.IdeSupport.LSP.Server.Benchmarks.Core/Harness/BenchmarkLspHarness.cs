#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nerdbank.Streams;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using LspCodeLens = OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;

/// <summary>
/// Drives the <em>real</em> Reqnroll LSP server through an OmniSharp client, exposing timed request
/// round-trips and timestamped server-push capture. Two transports:
/// <list type="bullet">
///   <item><see cref="StartAsync"/> — hosts the server <b>in-process</b> over an in-memory pipe
///   (fast, reproducible; no process/stdio boundary).</item>
///   <item><see cref="StartOutOfProcessAsync"/> — spawns the built server <b>exe</b> and talks to it
///   over <b>stdio</b> (the production transport; includes the real process boundary).</item>
/// </list>
/// Either way, latency is measured at the protocol boundary (serialize → transport → handler →
/// transport → deserialize), which is what the "from last didChange" targets require.
/// </summary>
public sealed class BenchmarkLspHarness : IAsyncDisposable
{
    private IDisposable? _server;
    private ILanguageClient? _client;
    private Process? _serverProcess;
    private readonly StringBuilder _serverStderr = new();
    private readonly object _stderrLock = new();

    private readonly object _diagLock = new();
    private readonly Dictionary<string, long> _lastDiagTimestamp = new(StringComparer.Ordinal);

    private readonly object _refreshLock = new();
    private long? _lastSemanticTokensRefreshTimestamp;
    private long? _lastInlayHintRefreshTimestamp;
    private long? _lastCodeLensRefreshTimestamp;

    public ILanguageClient Client =>
        _client ?? throw new InvalidOperationException("Harness not started.");

    /// <summary>Anything the spawned server wrote to stderr (out-of-process mode), for diagnosis.</summary>
    public string ServerStandardError { get { lock (_stderrLock) return _serverStderr.ToString(); } }

    /// <summary>Hosts the server in-process over an in-memory full-duplex pipe.</summary>
    public async Task StartAsync(string workspaceFolder, string? ideId = null)
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();

        var serverTask = LanguageServer.From(options =>
        {
            options.WithInput(serverStream).WithOutput(serverStream);
            Program.ConfigureServer(options, ideId);
        });

        _client = await CreateClientAsync(input: clientStream, output: clientStream, workspaceFolder)
            .ConfigureAwait(false);
        _server = await serverTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns the built server executable and connects over its stdio — the production transport,
    /// including the real OS process boundary (no in-memory shortcut). Slower to start than
    /// <see cref="StartAsync"/>, but the numbers include cross-process stdio and process isolation.
    /// </summary>
    public async Task StartOutOfProcessAsync(string workspaceFolder, string serverExePath, string? ideId = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = serverExePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(serverExePath) ?? Environment.CurrentDirectory,
        };
        if (!string.IsNullOrEmpty(ideId))
        {
            psi.ArgumentList.Add("--ide");
            psi.ArgumentList.Add(ideId);
        }

        _serverProcess = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start server process '{serverExePath}'.");

        // Drain stderr so the server can't block on a full pipe, and keep it for diagnosis.
        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_stderrLock) _serverStderr.AppendLine(e.Data);
        };
        _serverProcess.BeginErrorReadLine();

        // The client writes to the server's stdin and reads from its stdout; use the raw BaseStreams
        // so JSON-RPC framing isn't mangled by text-mode StreamReader/Writer translation.
        _client = await CreateClientAsync(
            input: _serverProcess.StandardOutput.BaseStream,
            output: _serverProcess.StandardInput.BaseStream,
            workspaceFolder).ConfigureAwait(false);
    }

    private async Task<ILanguageClient> CreateClientAsync(Stream input, Stream output, string workspaceFolder) =>
        await LanguageClient.From(options =>
        {
            options.WithInput(input).WithOutput(output);
            options.WithRootUri(DocumentUri.FromFileSystemPath(workspaceFolder));
            options.WithWorkspaceFolder(DocumentUri.FromFileSystemPath(workspaceFolder), "benchmark-workspace");

            // Advertise refresh support so SemanticTokensRefreshHandler / InlayHintRefreshHandler
            // (both capability-gated) actually send their debounced workspace/*/refresh requests to
            // this client — otherwise those two handlers have nothing to synthetically benchmark.
            options.ClientCapabilities.Workspace?.SemanticTokens =
                new Supports<SemanticTokensWorkspaceCapability>(true, new SemanticTokensWorkspaceCapability { RefreshSupport = true });
            options.ClientCapabilities.Workspace?.InlayHint =
                new Supports<InlayHintWorkspaceClientCapabilities>(true, new InlayHintWorkspaceClientCapabilities { RefreshSupport = true });

            // Timestamp every publishDiagnostics push so a didChange can be timed against the
            // resulting diagnostics for the matching URI.
            options.OnNotification("textDocument/publishDiagnostics", (PublishDiagnosticsParams p) =>
            {
                lock (_diagLock)
                    _lastDiagTimestamp[p.Uri.ToString()] = Stopwatch.GetTimestamp();
                return Task.CompletedTask;
            });

            // Both refresh requests carry no params and expect a void/null result (server sends them
            // via .ReturningVoid(...)) — just timestamp arrival and acknowledge.
            options.OnRequest(LspMethodNames.WorkspaceSemanticTokensRefresh, (CancellationToken _) =>
            {
                lock (_refreshLock) _lastSemanticTokensRefreshTimestamp = Stopwatch.GetTimestamp();
                return Task.CompletedTask;
            });
            options.OnRequest(LspMethodNames.WorkspaceInlayHintRefresh, (CancellationToken _) =>
            {
                lock (_refreshLock) _lastInlayHintRefreshTimestamp = Stopwatch.GetTimestamp();
                return Task.CompletedTask;
            });
            // Unlike the two above, workspace/codeLens/refresh is not capability-gated — the server
            // sends it unconditionally (see BindingRegistryChangedHandler.RequestCodeLensRefreshAsync),
            // so no ClientCapabilities.Workspace.CodeLens advertisement is needed for this to fire.
            options.OnRequest(LspMethodNames.WorkspaceCodeLensRefresh, (CancellationToken _) =>
            {
                lock (_refreshLock) _lastCodeLensRefreshTimestamp = Stopwatch.GetTimestamp();
                return Task.CompletedTask;
            });
        }).ConfigureAwait(false);

    // ── Document lifecycle ──────────────────────────────────────────────────────

    public void OpenFeature(DocumentUri uri, int version, string text) =>
        Client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            { Uri = uri, Version = version, LanguageId = "Gherkin", Text = text }
        });

    public void ChangeFeature(DocumentUri uri, int version, string text) =>
        Client.SendNotification("textDocument/didChange", new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = text })
        });

    public void OpenCSharp(DocumentUri uri, int version, string text) =>
        Client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            { Uri = uri, Version = version, LanguageId = "csharp", Text = text }
        });

    public void ChangeCSharp(DocumentUri uri, int version, string text) =>
        Client.SendNotification("textDocument/didChange", new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = text })
        });

    public void SendNotification(string method, object payload) => Client.SendNotification(method, payload);

    /// <summary>
    /// Announces the corpus as a loaded Reqnroll project so binding discovery runs against the
    /// built corpus bindings assembly (see <c>CorpusAssemblyLocator</c>) and the registry is
    /// primed for bound-state benchmarks (definition cache-hit, step completion), instead of
    /// running against an empty registry.
    /// </summary>
    public void SendCorpusProjectLoaded(string corpusRoot, string corpusAssemblyPath) =>
        Client.SendNotification("reqnroll/projectLoaded", new
        {
            workspaceFolder = corpusRoot,
            projectFile = Path.Combine(corpusRoot, "Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus.csproj"),
            projectFolder = corpusRoot,
            outputAssemblyPath = corpusAssemblyPath,
            targetFrameworkMoniker = ".NETCoreApp,Version=v10.0",
            packageReferences = Array.Empty<object>(),
        });

    // ── Timed request ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a request and returns the wall-clock round-trip in milliseconds (the response is
    /// awaited but discarded; scenarios that need the payload use <see cref="RequestAsync{T}"/>).
    /// </summary>
    public async Task<double> TimeRequestAsync<TResp>(string method, object @params, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        await Client.SendRequest(method, @params).Returning<TResp>(ct).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public Task<TResp> RequestAsync<TResp>(string method, object @params, CancellationToken ct = default) =>
        Client.SendRequest(method, @params).Returning<TResp>(ct);

    // ── Typed request helpers (used by the editing-session scenario's bursts) ────
    // Each takes a CancellationToken: cancelling it sends $/cancelRequest on the wire, modelling a
    // client superseding an in-flight request when the user types again.

    public Task<SemanticTokens?> RequestSemanticTokensAsync(DocumentUri uri, CancellationToken ct = default) =>
        RequestAsync<SemanticTokens?>("textDocument/semanticTokens/full",
            new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } }, ct);

    public Task<CompletionList?> RequestCompletionAsync(DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<CompletionList?>("textDocument/completion",
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    public Task<LocationOrLocationLinks?> RequestDefinitionAsync(DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<LocationOrLocationLinks?>("textDocument/definition",
            new DefinitionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    public Task<SymbolInformationOrDocumentSymbolContainer?> RequestDocumentSymbolAsync(DocumentUri uri, CancellationToken ct = default) =>
        RequestAsync<SymbolInformationOrDocumentSymbolContainer?>("textDocument/documentSymbol",
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = uri } }, ct);

    public Task<Container<FoldingRange>?> RequestFoldingRangeAsync(DocumentUri uri, CancellationToken ct = default) =>
        RequestAsync<Container<FoldingRange>?>("textDocument/foldingRange",
            new FoldingRangeRequestParam { TextDocument = new TextDocumentIdentifier { Uri = uri } }, ct);

    public Task<SemanticTokensFullOrDelta?> RequestSemanticTokensDeltaAsync(
        DocumentUri uri, string previousResultId, CancellationToken ct = default) =>
        RequestAsync<SemanticTokensFullOrDelta?>(LspMethodNames.TextDocumentSemanticTokensFullDelta,
            new SemanticTokensDeltaParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                PreviousResultId = previousResultId,
            }, ct);

    // ── Rename (F16) ─────────────────────────────────────────────────────────────

    public Task<RangeOrPlaceholderRange?> RequestPrepareRenameAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<RangeOrPlaceholderRange?>(LspMethodNames.TextDocumentPrepareRename,
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    public Task<WorkspaceEdit?> RequestRenameAsync(
        DocumentUri uri, int line, int character, string newName, CancellationToken ct = default) =>
        RequestAsync<WorkspaceEdit?>(LspMethodNames.TextDocumentRename,
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
                NewName = newName,
            }, ct);

    public Task<RenameTargetsResponse?> RequestRenameTargetsAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<RenameTargetsResponse?>(LspMethodNames.ReqnrollRenameTargets,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    // ── Find unused step definitions (F15) ──────────────────────────────────────

    public Task<FindUnusedStepDefinitionsResponse?> RequestFindUnusedStepDefinitionsAsync(CancellationToken ct = default) =>
        RequestAsync<FindUnusedStepDefinitionsResponse?>(
            LspMethodNames.ReqnrollFindUnusedStepDefinitions, new FindUnusedStepDefinitionsParams(), ct);

    // ── References / go-to (F5/F17) ─────────────────────────────────────────────

    public Task<FindStepUsagesResponse?> RequestFindStepUsagesAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<FindStepUsagesResponse?>(LspMethodNames.ReqnrollFindStepUsages,
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
                Context = new ReferenceContext { IncludeDeclaration = true },
            }, ct);

    public Task<LocationOrLocationLinks?> RequestStepReferencesAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<LocationOrLocationLinks?>(LspMethodNames.TextDocumentReferences,
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
                Context = new ReferenceContext { IncludeDeclaration = true },
            }, ct);

    public Task<GoToHooksResponse?> RequestGoToHooksAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<GoToHooksResponse?>(LspMethodNames.ReqnrollGoToHooks,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    /// <summary>
    /// <c>reqnroll/goToMatchingScenarios</c> (issue #373) — the inverse of
    /// <see cref="RequestGoToHooksAsync"/>: given a `.cs` position pinned to a hook-binding
    /// attribute, returns every scenario its scope matches. <paramref name="line"/>/
    /// <paramref name="character"/> must be the attribute's exact position (0-based), same as a
    /// real client round-trips verbatim from a <see cref="RequestCodeLensAsync"/> hook lens's own
    /// click arguments — not a proximity search like <see cref="RequestGoToHooksAsync"/>'s cursor
    /// resolution.
    /// </summary>
    public Task<GoToMatchingScenariosResponse?> RequestGoToMatchingScenariosAsync(
        DocumentUri uri, int line, int character, CancellationToken ct = default) =>
        RequestAsync<GoToMatchingScenariosResponse?>(LspMethodNames.ReqnrollGoToMatchingScenarios,
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
            }, ct);

    /// <summary>
    /// <c>reqnroll/resolveTestTargets</c> (issue #262/#495) — resolves the generated test method(s)
    /// for the scenario/Outline/example-row header at <paramref name="range"/> in <paramref name="uri"/>.
    /// Backs the Run CodeLens bridge on all three IDE clients; unlike <see cref="RequestGoToHooksAsync"/>
    /// (cursor position), this takes an explicit range the same way a real client's per-line
    /// resolution does (VS's <c>RunTestCodeLensDataPoint</c>, VS Code's <c>resolveCodeLens</c>,
    /// Rider's <c>RunLensSupport</c> — see issue #495's per-client split).
    /// </summary>
    public Task<ResolveTestTargetsResponse?> RequestResolveTestTargetsAsync(
        DocumentUri uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, CancellationToken ct = default) =>
        RequestAsync<ResolveTestTargetsResponse?>(LspMethodNames.ReqnrollResolveTestTargets,
            new ResolveTestTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = range,
            }, ct);

    // ── Code lens (F18), inlay hints (F23), code actions (F6) ───────────────────

    public Task<LspCodeLens[]?> RequestCodeLensAsync(DocumentUri uri, CancellationToken ct = default) =>
        RequestAsync<LspCodeLens[]?>(LspMethodNames.TextDocumentCodeLens,
            new CodeLensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } }, ct);

    public Task<InlayHintContainer?> RequestInlayHintAsync(
        DocumentUri uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, CancellationToken ct = default) =>
        RequestAsync<InlayHintContainer?>(LspMethodNames.TextDocumentInlayHint,
            new InlayHintParams { TextDocument = new TextDocumentIdentifier { Uri = uri }, Range = range }, ct);

    public Task<CommandOrCodeActionContainer?> RequestCodeActionAsync(
        DocumentUri uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, CancellationToken ct = default) =>
        RequestAsync<CommandOrCodeActionContainer?>(LspMethodNames.TextDocumentCodeAction,
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = range,
                Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() },
            }, ct);

    // ── Formatting (F11/F12) ─────────────────────────────────────────────────────

    private static readonly FormattingOptions DefaultFormattingOptions = new() { TabSize = 2, InsertSpaces = true };

    public Task<TextEditContainer?> RequestDocumentFormattingAsync(DocumentUri uri, CancellationToken ct = default) =>
        RequestAsync<TextEditContainer?>(LspMethodNames.TextDocumentFormatting,
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Options = DefaultFormattingOptions,
            }, ct);

    public Task<TextEditContainer?> RequestRangeFormattingAsync(
        DocumentUri uri, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, CancellationToken ct = default) =>
        RequestAsync<TextEditContainer?>(LspMethodNames.TextDocumentRangeFormatting,
            new DocumentRangeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = range,
                Options = DefaultFormattingOptions,
            }, ct);

    public Task<TextEditContainer?> RequestOnTypeFormattingAsync(
        DocumentUri uri, int line, int character, string triggerCharacter, CancellationToken ct = default) =>
        RequestAsync<TextEditContainer?>(LspMethodNames.TextDocumentOnTypeFormatting,
            new DocumentOnTypeFormattingParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position(line, character),
                Character = triggerCharacter,
                Options = DefaultFormattingOptions,
            }, ct);

    // ── Watched files / config reconciliation ───────────────────────────────────

    /// <summary>
    /// Simulates the client's file-watcher reporting a <c>reqnroll.json</c> change — the trigger for
    /// <c>WatchedFilesHandler</c> → <c>ReqnrollConfigChangedHandler</c> → per-file re-diagnose. Fire
    /// and forget, like every other <c>workspace/didChangeWatchedFiles</c> notification.
    /// </summary>
    public void SendConfigFileChanged(DocumentUri reqnrollJsonUri) =>
        Client.SendNotification(LspMethodNames.WorkspaceDidChangeWatchedFiles, new DidChangeWatchedFilesParams
        {
            Changes = new Container<FileEvent>(new FileEvent { Uri = reqnrollJsonUri, Type = FileChangeType.Changed }),
        });

    // ── Server-initiated refresh push timing ────────────────────────────────────
    // Mirrors WaitForDiagnosticsAsync: the client capabilities above make the server actually send
    // these debounced (500ms) workspace/*/refresh requests; these wait for the next one after a
    // given origin timestamp.

    public Task<double?> WaitForSemanticTokensRefreshAsync(long sinceTimestamp, int timeoutMs = 3000) =>
        WaitForRefreshAsync(() => _lastSemanticTokensRefreshTimestamp, sinceTimestamp, timeoutMs);

    public Task<double?> WaitForInlayHintRefreshAsync(long sinceTimestamp, int timeoutMs = 3000) =>
        WaitForRefreshAsync(() => _lastInlayHintRefreshTimestamp, sinceTimestamp, timeoutMs);

    // Not capability-gated (see the OnRequest registration above), but otherwise debounced (500ms)
    // the same way — triggered by BindingRegistryChangedHandler's incremental-Roslyn-patch path,
    // not by a plain .feature edit, so callers must edit a .cs binding file to exercise this one.
    public Task<double?> WaitForCodeLensRefreshAsync(long sinceTimestamp, int timeoutMs = 3000) =>
        WaitForRefreshAsync(() => _lastCodeLensRefreshTimestamp, sinceTimestamp, timeoutMs);

    private async Task<double?> WaitForRefreshAsync(Func<long?> readTimestamp, long sinceTimestamp, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_refreshLock)
            {
                var ts = readTimestamp();
                if (ts is { } value && value >= sinceTimestamp)
                    return Stopwatch.GetElapsedTime(sinceTimestamp, value).TotalMilliseconds;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        return null;
    }

    // ── Diagnostics push timing ─────────────────────────────────────────────────

    /// <summary>
    /// Waits until a <c>publishDiagnostics</c> for <paramref name="uri"/> arrives after
    /// <paramref name="sinceTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value), and returns
    /// the elapsed milliseconds from that origin to the push. Returns <c>null</c> on timeout.
    /// </summary>
    public async Task<double?> WaitForDiagnosticsAsync(DocumentUri uri, long sinceTimestamp, int timeoutMs = 5000)
    {
        var key = uri.ToString();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_diagLock)
            {
                if (_lastDiagTimestamp.TryGetValue(key, out var ts) && ts >= sinceTimestamp)
                    return Stopwatch.GetElapsedTime(sinceTimestamp, ts).TotalMilliseconds;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try { (_client as IDisposable)?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        if (_serverProcess is not null)
        {
            try { if (!_serverProcess.HasExited) _serverProcess.Kill(entireProcessTree: true); } catch { }
            try { _serverProcess.Dispose(); } catch { }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
