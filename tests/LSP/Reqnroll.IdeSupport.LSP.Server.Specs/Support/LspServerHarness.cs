using Nerdbank.Streams;
using Newtonsoft.Json.Linq;                                            // JToken (custom notification capture)
using OmniSharp.Extensions.LanguageServer.Client;                      // LanguageClient factory + option extensions
using OmniSharp.Extensions.LanguageServer.Protocol;                    // WorkspaceNames, DocumentUri
using OmniSharp.Extensions.LanguageServer.Protocol.Client;             // ILanguageClient
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;// SemanticTokensWorkspaceCapability
using OmniSharp.Extensions.LanguageServer.Protocol.Document;           // OnPublishDiagnostics
using OmniSharp.Extensions.LanguageServer.Protocol.Models;             // InitializeResult
using OmniSharp.Extensions.LanguageServer.Server;                      // LanguageServer factory
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Hosts the <em>real</em> Reqnroll LSP server in-process over an in-memory full-duplex pipe
/// and connects an OmniSharp <see cref="ILanguageClient"/> to it, so specs can exercise the
/// actual LSP wire protocol (initialize, didOpen, semanticTokens, custom reqnroll/* notifications,
/// workspace/semanticTokens/refresh) end-to-end.
/// </summary>
/// <remarks>
/// One harness per scenario; Reqnroll disposes it at scenario end.  The server transport is
/// supplied by the spec rather than stdio thanks to <see cref="Program.ConfigureServer"/> being
/// transport-agnostic.
/// </remarks>
public sealed class LspServerHarness : IAsyncDisposable
{
    private IDisposable? _server;
    private ILanguageClient? _client;
    private readonly object _refreshLock = new();
    private int _refreshCount;
    private TaskCompletionSource<int> _refreshSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _pushLock = new();
    private readonly List<(string Uri, int TokenCount)> _pushes = new();
    private TaskCompletionSource<int> _pushSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ILanguageClient Client =>
        _client ?? throw new InvalidOperationException("Harness not started.");

    /// <summary>The InitializeResult returned by the server (capabilities, server info).</summary>
    public InitializeResult ServerInitializeResult => Client.ServerSettings;

    /// <summary>Number of workspace/semanticTokens/refresh requests received so far.</summary>
    public int RefreshCount { get { lock (_refreshLock) return _refreshCount; } }

    /// <summary>The <c>reqnroll/semanticTokens</c> push notifications received so far (uri + token count).</summary>
    public IReadOnlyList<(string Uri, int TokenCount)> SemanticTokenPushes
    {
        get { lock (_pushLock) return _pushes.ToArray(); }
    }

    private readonly object _applyEditLock = new();
    private ApplyWorkspaceEditParams? _lastApplyEdit;
    private TaskCompletionSource<int> _applyEditSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApplyWorkspaceEditParams? LastApplyEdit
    {
        get { lock (_applyEditLock) return _lastApplyEdit; }
    }

    private readonly object _diagnosticsLock = new();
    private readonly Dictionary<string, PublishDiagnosticsParams> _diagnostics =
        new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<int> _diagnosticsSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The most recent <c>textDocument/publishDiagnostics</c> notification for a URI, or null if
    /// the server has not published for it at all. LSP defines each push as the <em>complete</em>
    /// set for that URI, so only the latest is kept — and "published an empty set" (diagnostics
    /// cleared) is deliberately distinguishable from "never published".
    /// </summary>
    public PublishDiagnosticsParams? PublishedDiagnosticsFor(DocumentUri uri)
    {
        lock (_diagnosticsLock)
            return _diagnostics.TryGetValue(uri.ToString(), out var p) ? p : null;
    }

    public async Task StartAsync(string workspaceFolder, string? ideId = null, bool supportsChangeAnnotations = false)
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();

        // Start the server first (do not await yet — From() completes once the client's
        // initialize handshake lands).  The --ide identifier no longer affects the semantic
        // token legend, but is still threaded through to exercise the startup plumbing.
        var serverTask = LanguageServer.From(options =>
        {
            options.WithInput(serverStream).WithOutput(serverStream);
            Program.ConfigureServer(options, ideId);
        });

        _client = await LanguageClient.From(options =>
        {
            options.WithInput(clientStream).WithOutput(clientStream);
            options.WithRootUri(DocumentUri.FromFileSystemPath(workspaceFolder));
            options.WithWorkspaceFolder(DocumentUri.FromFileSystemPath(workspaceFolder), "test-workspace");

            // Advertise refresh support — the server's SemanticTokensRefreshHandler skips the
            // request unless workspace.semanticTokens.refreshSupport is true.
            options.WithCapability(new SemanticTokensWorkspaceCapability { RefreshSupport = true });

            // Issue #70: opt-in only (defaults to false) so every other spec keeps negotiating
            // the legacy WorkspaceEdit.Changes shape unchanged — only scenarios that explicitly
            // start the harness this way exercise RenameHandler's annotated DocumentChanges path.
            if (supportsChangeAnnotations)
            {
                options.WithCapability(new WorkspaceEditCapability
                {
                    DocumentChanges = true,
                    ChangeAnnotationSupport = new WorkspaceEditSupportCapabilitiesChangeAnnotationSupport()
                });
            }

            // Sink for the server-initiated refresh request.
            options.OnRequest(WorkspaceNames.SemanticTokensRefresh, (CancellationToken _) =>
            {
                RecordRefresh();
                return Task.CompletedTask;
            });

            // Sink for the VS-only server-push notification carrying encoded tokens.
            options.OnNotification("reqnroll/semanticTokens", (JToken p) =>
            {
                var uri = p["uri"]?.Value<string>() ?? string.Empty;
                var count = (p["data"] as JArray)?.Count / 5 ?? 0;
                RecordPush(uri, count);
                return Task.CompletedTask;
            });

            // Sink for textDocument/publishDiagnostics (F3 parse errors, F4 undefined/ambiguous
            // steps). Server-initiated notification; nothing is sent back.
            options.OnPublishDiagnostics(RecordDiagnostics);

            // Sink for workspace/applyEdit (F13 — Comment/Uncomment).
            // LSP defines this as a server-initiated request (not notification) — client must respond.
            options.OnRequest<ApplyWorkspaceEditParams, ApplyWorkspaceEditResponse>(
                "workspace/applyEdit",
                (p, _) =>
                {
                    RecordApplyEdit(p);
                    return Task.FromResult(new ApplyWorkspaceEditResponse { Applied = true });
                });
        }).ConfigureAwait(false);

        _server = await serverTask.ConfigureAwait(false);
    }

    private void RecordRefresh()
    {
        lock (_refreshLock)
        {
            _refreshCount++;
            var prev = _refreshSignal;
            _refreshSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(_refreshCount);
        }
    }

    private void RecordPush(string uri, int tokenCount)
    {
        lock (_pushLock)
        {
            _pushes.Add((uri, tokenCount));
            var prev = _pushSignal;
            _pushSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(_pushes.Count);
        }
    }

    private void RecordApplyEdit(ApplyWorkspaceEditParams p)
    {
        lock (_applyEditLock)
        {
            _lastApplyEdit = p;
            var prev = _applyEditSignal;
            _applyEditSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(1);
        }
    }

    private void RecordDiagnostics(PublishDiagnosticsParams p)
    {
        lock (_diagnosticsLock)
        {
            _diagnostics[p.Uri.ToString()] = p;
            var prev = _diagnosticsSignal;
            _diagnosticsSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            prev.TrySetResult(_diagnostics.Count);
        }
    }

    /// <summary>
    /// Waits until the latest published diagnostics for <paramref name="uri"/> satisfy
    /// <paramref name="predicate"/>, or the timeout elapses. The predicate is given null while
    /// nothing has been published for the URI yet, so a caller can wait for the first push
    /// (<c>p =&gt; p is not null</c>) or for a particular diagnostic within it.
    /// <para>
    /// Polling is required rather than a single await: diagnostics reach the client through the
    /// asynchronous match-cache pipeline (tagger → MatchCacheChangedNotification →
    /// DiagnosticsPublishHandler), and the set for a URI is republished whenever bindings change,
    /// so the first push is not necessarily the settled one.
    /// </para>
    /// </summary>
    public async Task<bool> WaitForDiagnosticsAsync(
        DocumentUri uri,
        Func<PublishDiagnosticsParams?, bool> predicate,
        int timeoutMs = 5000)
    {
        var key = uri.ToString();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Task<int> wait;
            lock (_diagnosticsLock)
            {
                _diagnostics.TryGetValue(key, out var current);
                if (predicate(current)) return true;
                wait = _diagnosticsSignal.Task;
            }
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) return false;
            var completed = await Task.WhenAny(wait, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != wait)
            {
                lock (_diagnosticsLock)
                {
                    _diagnostics.TryGetValue(key, out var current);
                    return predicate(current);
                }
            }
        }
    }

    /// <summary>
    /// Waits until no further <c>publishDiagnostics</c> notification has arrived for any URI for
    /// <paramref name="quietMs"/>, or the timeout elapses; returns true if it went quiet.
    /// <para>
    /// Needed for any assertion about the <em>absence</em> of a diagnostic. The pipeline
    /// republishes as bindings change, and intermediate states are genuinely observable on the
    /// wire — a step can be briefly reported undefined while a registry update is still
    /// propagating. Asserting on the first set that arrives would turn that flicker into a
    /// failure and read as a lost binding, so absence is only ever asserted once the stream has
    /// settled.
    /// </para>
    /// </summary>
    public async Task<bool> WaitForDiagnosticsQuiescenceAsync(int quietMs = 750, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Task<int> wait;
            lock (_diagnosticsLock) wait = _diagnosticsSignal.Task;

            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            var quiet = Math.Min(quietMs, Math.Max(0, remaining));
            var completed = await Task.WhenAny(wait, Task.Delay(quiet)).ConfigureAwait(false);

            // The quiet window elapsed with no new publish — the stream has settled.
            if (completed != wait)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Waits until a <c>reqnroll/semanticTokens</c> push whose URI satisfies <paramref name="uriMatch"/>
    /// has been received, or the timeout elapses. Returns true if one arrived.
    /// </summary>
    public async Task<bool> WaitForPushAsync(Func<string, bool> uriMatch, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Task<int> wait;
            lock (_pushLock)
            {
                if (_pushes.Any(p => uriMatch(p.Uri))) return true;
                wait = _pushSignal.Task;
            }
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) return false;
            var completed = await Task.WhenAny(wait, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != wait)
            {
                lock (_pushLock) return _pushes.Any(p => uriMatch(p.Uri));
            }
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="minCount"/> refresh requests have been received,
    /// or the timeout elapses.  Returns true if the threshold was reached.
    /// </summary>
    public async Task<bool> WaitForRefreshAsync(int minCount = 1, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Task<int> wait;
            lock (_refreshLock)
            {
                if (_refreshCount >= minCount) return true;
                wait = _refreshSignal.Task;
            }
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) return false;
            var completed = await Task.WhenAny(wait, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != wait) return RefreshCount >= minCount;
        }
    }

    /// <summary>
    /// Waits until no further <c>workspace/semanticTokens/refresh</c> request has arrived for
    /// <paramref name="quietMs"/>, or the timeout elapses; returns true if it went quiet. Use
    /// before asserting on <see cref="RefreshCount"/>: the refresh is debounced, so a count read
    /// while the window is still open measures how fast the assertion ran, not how well the
    /// server coalesced.
    /// </summary>
    public async Task<bool> WaitForRefreshQuiescenceAsync(int quietMs = 1500, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Task<int> wait;
            lock (_refreshLock) wait = _refreshSignal.Task;

            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            var quiet = Math.Min(quietMs, Math.Max(0, remaining));
            var completed = await Task.WhenAny(wait, Task.Delay(quiet)).ConfigureAwait(false);

            if (completed != wait)
                return true;
        }
        return false;
    }

    public ValueTask DisposeAsync()
    {
        try { (_client as IDisposable)?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
