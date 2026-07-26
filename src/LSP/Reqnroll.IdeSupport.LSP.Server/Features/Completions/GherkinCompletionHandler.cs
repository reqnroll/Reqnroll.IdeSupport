#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;



using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Completions;

/// <summary>
/// Handles <c>textDocument/completion</c> requests for <c>*.feature</c> files.
/// Implements both Gherkin keyword completion and step-definition sample completion.
/// Registered via OmniSharp dynamic registration (<see cref="ICompletionHandler"/>), scoped to
/// <c>**/*.feature</c> documents so it does not conflict with the C# language server.
/// </summary>
public sealed class GherkinCompletionHandler : ICompletionHandler
{
    private readonly ICompletionContextResolver    _contextResolver;
    private readonly ICompletionService            _completionService;
    private readonly ICompletionMatcher            _matcher;
    private readonly IBindingMatchService          _matchService;
    private readonly IDocumentBufferService        _bufferService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly ClientIdeContext              _clientIde;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;

    // Performance Verification (Layer 4) op labels. Keyword completion (<50ms) and step completion
    // (<150ms) have distinct targets, so they are recorded under distinct operation names.
    private const string KeywordCompletionOp = LspMethodNames.TextDocumentCompletion + "#keyword";
    private const string StepCompletionOp    = LspMethodNames.TextDocumentCompletion + "#step";

    /// <summary>Initializes a new instance of the <see cref="GherkinCompletionHandler"/> class.</summary>
    public GherkinCompletionHandler(
        ICompletionContextResolver    contextResolver,
        ICompletionService            completionService,
        ICompletionMatcher            matcher,
        IBindingMatchService          matchService,
        IDocumentBufferService        bufferService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        ClientIdeContext              clientIde,
        IIdeSupportLogger               logger,
        IOperationDurationRecorder?   recorder = null)
    {
        _contextResolver   = contextResolver;
        _completionService = completionService;
        _matcher           = matcher;
        _matchService      = matchService;
        _bufferService     = bufferService;
        _scopeManager      = scopeManager;
        _registryLookup    = registryLookup;
        _clientIde         = clientIde;
        _logger            = logger;
        _recorder          = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Builds the LSP registration options advertising completion support (no resolve step) for <c>.feature</c> files.</summary>
    public CompletionRegistrationOptions GetRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities   clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.feature" }),
            ResolveProvider  = false
        };

    /// <summary>Handles a <c>textDocument/completion</c> request for Gherkin completions.</summary>
    public Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"GherkinCompletionHandler: ignoring non-.feature URI {uri}");
            return Task.FromResult(new CompletionList());
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"GherkinCompletionHandler: no document buffer for {uri}");
            return Task.FromResult(new CompletionList());
        }

        var snapshot   = buffer.ToGherkinTextSnapshot();
        var cursorLine = request.Position.Line;
        var cursorChar = request.Position.Character;

        cancellationToken.ThrowIfCancellationRequested();

        var registry         = _registryLookup.GetRegistryForUri(uri);
        var fallbackLanguage = GetFallbackLanguage(uri);

        var ctx = _contextResolver.Resolve(snapshot, cursorLine, cursorChar, registry, fallbackLanguage);

        // Performance Verification (Layer 4): time the completion compute, recording under the kind-specific op label
        // (keyword vs. step) so field P95 can be compared to the two distinct targets.
        var startTimestamp = Stopwatch.GetTimestamp();
        var (op, list) = ctx switch
        {
            StepCompletionContext    s => (StepCompletionOp,    HandleStep(s,    uri, cursorLine, snapshot)),
            KeywordCompletionContext k => (KeywordCompletionOp, HandleKeyword(k, cursorLine, cursorChar, snapshot)),
            _                          => (LspMethodNames.TextDocumentCompletion, new CompletionList())
        };
        _recorder.Record(op, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, uri);

        return Task.FromResult(list);
    }

    // ── Step-definition sample completion ────────────────────────────────────

    private CompletionList HandleStep(
        StepCompletionContext s,
        DocumentUri          uri,
        int                  cursorLine,
        IGherkinTextSnapshot snapshot)
    {
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var registry = _registryLookup.GetRegistryForUri(uri);

        Func<ProjectStepDefinitionBinding, int> usageCounter = sd =>
            sd.Implementation?.SourceLocation is { } loc
                ? _matchService.FindUsages(loc, projectFilter).Count
                : 0;

        var result = _completionService.GetStepCompletions(
            s.Step, s.TypedAfterKeyword, registry, usageCounter, _matcher);

        var snapshotLine = snapshot.GetLineFromLineNumber(cursorLine);
        var lineLength   = snapshotLine.End - snapshotLine.Start;
        var stepRange    = new LspRange(
            new Position(cursorLine, s.StepTextStartColumn),
            new Position(cursorLine, lineLength));

        _logger.LogVerbose(
            $"GherkinCompletionHandler: {result.Entries.Count} step completion(s) for {uri}");
        return new CompletionList(ToItems(result.Entries, stepRange));
    }

    // ── Gherkin keyword completion ────────────────────────────────────────────

    private CompletionList HandleKeyword(
        KeywordCompletionContext k,
        int                     cursorLine,
        int                     cursorChar,
        IGherkinTextSnapshot    snapshot)
    {
        // Replacement range: first non-whitespace → end of current word + trailing whitespace
        var lineText = snapshot.GetLineFromLineNumber(cursorLine).GetText();

        // A partial table row like "|4" causes the Gherkin AST to fall through to keyword
        // suggestions (including @tags, block keywords), which mangle the row when Tab accepts
        // the top completion. Suppress keyword completions for table rows entirely.
        if (lineText.TrimStart().StartsWith("|", StringComparison.Ordinal))
        {
            _logger.LogVerbose("GherkinCompletionHandler: table row — suppressing keyword completions");
            return BuildTableRowSuppressionResult(cursorLine, cursorChar);
        }

        var kwResult = k.ExpectedTokens.Length > 0
            ? _completionService.GetKeywordCompletions(k.ExpectedTokens, k.Dialect)
            : _completionService.GetDefaultKeywordCompletions(k.Dialect);
        var kwStart  = 0;
        while (kwStart < lineText.Length && char.IsWhiteSpace(lineText[kwStart]))
            kwStart++;
        var kwEnd = lineText.Length;
        while (kwEnd > kwStart && char.IsWhiteSpace(lineText[kwEnd - 1]))
            kwEnd--;
        var kwRange = new LspRange(
            new Position(cursorLine, kwStart),
            new Position(cursorLine, kwEnd));

        _logger.LogVerbose(
            $"GherkinCompletionHandler: {kwResult.Entries.Count} keyword completion(s)");
        return new CompletionList(ToItems(kwResult.Entries, kwRange));
    }

    /// <summary>
    /// Builds the result for a suppressed table-row keyword completion, per-IDE.
    /// </summary>
    /// <remarks>
    /// VS 2022 treats an empty <see cref="CompletionList"/> for a trigger-character request as
    /// "reject and revert the typed character" — returning an empty list would delete the '|'
    /// from the document. Offer a no-op cell-separator item instead so VS accepts the character.
    /// Every other client handles an empty list correctly, so they get the plain empty result.
    /// </remarks>
    private CompletionList BuildTableRowSuppressionResult(int cursorLine, int cursorChar)
    {
        if (!_clientIde.IsVisualStudio)
            return new CompletionList();

        var insertPos = new Position(cursorLine, cursorChar);
        return new CompletionList(new[]
        {
            new CompletionItem
            {
                Label    = "| ",
                Detail   = "Table cell separator",
                Kind     = (CompletionItemKind)(int)CompletionEntryKind.Keyword,
                TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit
                {
                    Range   = new LspRange(insertPos, insertPos),
                    NewText = "| "
                })
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetFallbackLanguage(DocumentUri uri)
    {
        var configProvider = _scopeManager.GetConfigurationProviderForUri(uri);
        return configProvider.GetConfiguration()?.DefaultFeatureLanguage ?? "en";
    }

    private static List<CompletionItem> ToItems(IReadOnlyList<CompletionEntry> entries, LspRange range)
        => entries
            .Select(e => new CompletionItem
            {
                Label      = e.Label,
                Detail     = e.Detail,
                Kind       = (CompletionItemKind)(int)e.Kind,
                SortText   = e.SortText,
                FilterText = e.FilterText,
                TextEdit   = new TextEditOrInsertReplaceEdit(new TextEdit
                {
                    Range   = range,
                    NewText = e.InsertText ?? e.Label
                })
            })
            .ToList();

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}
