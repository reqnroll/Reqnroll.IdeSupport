#nullable enable

using Gherkin;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.Bindings;



using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
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
public sealed class CompletionHandler : ICompletionHandler
{
    private readonly ICompletionContextResolver _contextResolver;
    private readonly ICompletionService _completionService;
    private readonly ICompletionMatcher _matcher;
    private readonly IBindingMatchService _matchService;
    private readonly IDocumentBufferService _bufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IFeatureTagIndex _tagIndex;
    private readonly ClientIdeContext _clientIde;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    // Performance Verification (Layer 4) op labels. Keyword completion (<50ms) and step completion
    // (<150ms) have distinct targets, so they are recorded under distinct operation names.
    private const string KeywordCompletionOp = LspStandardMethodNames.TextDocumentCompletion + "#keyword";
    private const string StepCompletionOp = LspStandardMethodNames.TextDocumentCompletion + "#step";

    /// <summary>Initializes a new instance of the <see cref="CompletionHandler"/> class.</summary>
    public CompletionHandler(
        ICompletionContextResolver contextResolver,
        ICompletionService completionService,
        ICompletionMatcher matcher,
        IBindingMatchService matchService,
        IDocumentBufferService bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IFeatureTagIndex tagIndex,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _contextResolver = contextResolver;
        _completionService = completionService;
        _matcher = matcher;
        _matchService = matchService;
        _bufferService = bufferService;
        _scopeManager = scopeManager;
        _registryLookup = registryLookup;
        _tagIndex = tagIndex;
        _clientIde = clientIde;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>
    /// Characters that make the client request completion by themselves, beyond the identifier
    /// characters it always triggers on. A space is needed so the popup also opens after the
    /// whitespace that separates a completed tag from the next one, or that precedes a tag on a
    /// blank line — clients do not request completion on Enter or on a space unless it is
    /// declared here (they do on backspace, which is why deleting the space used to "fix" it).
    /// </summary>
    internal static readonly string[] TriggerCharacters = { "@", " " };

    /// <summary>Builds the LSP registration options advertising completion support (no resolve step) for <c>.feature</c> files.</summary>
    public CompletionRegistrationOptions GetRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = DocumentGlobPatterns.FeatureFilePattern }),
            ResolveProvider = false,
            TriggerCharacters = new Container<string>(TriggerCharacters)
        };

    /// <summary>Handles a <c>textDocument/completion</c> request for Gherkin completions.</summary>
    public async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        if (!IsFeatureFile(uri))
        {
            _logger.LogVerbose($"CompletionHandler: ignoring non-.feature URI {uri}");
            return new CompletionList();
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"CompletionHandler: no document buffer for {uri}");
            return new CompletionList();
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var cursorLine = request.Position.Line;
        var cursorChar = request.Position.Character;

        cancellationToken.ThrowIfCancellationRequested();

        var registry = _registryLookup.GetRegistryForUri(uri);
        var fallbackLanguage = GetFallbackLanguage(uri);

        var ctx = _contextResolver.Resolve(snapshot, cursorLine, cursorChar, registry, fallbackLanguage);

        // Performance Verification (Layer 4): time the completion compute, recording under the kind-specific op label
        // (keyword vs. step) so field P95 can be compared to the two distinct targets.
        var startTimestamp = Stopwatch.GetTimestamp();
        CompletionList list;
        string op;
        switch (ctx)
        {
            case StepCompletionContext s:
                op = StepCompletionOp;
                list = HandleStep(s, uri, cursorLine, snapshot);
                break;
            case KeywordCompletionContext k:
                op = KeywordCompletionOp;
                list = await HandleKeywordAsync(k, uri, cursorLine, cursorChar, snapshot, cancellationToken);
                break;
            default:
                op = LspStandardMethodNames.TextDocumentCompletion;
                list = new CompletionList();
                break;
        }
        _recorder.Record(op, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, uri);

        return list;
    }

    // ── Step-definition sample completion ────────────────────────────────────

    private CompletionList HandleStep(
        StepCompletionContext s,
        DocumentUri uri,
        int cursorLine,
        IGherkinTextSnapshot snapshot)
    {
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var registry = _registryLookup.GetRegistryForUri(uri);

        Func<ProjectStepDefinitionBinding, int> usageCounter = sd =>
            sd.Implementation?.SourceLocation is not null
                ? _matchService.FindUsages(BindingId.For(sd), projectFilter).Count
                : 0;

        var result = _completionService.GetStepCompletions(
            s.Step, s.TypedAfterKeyword, registry, usageCounter, _matcher);

        var snapshotLine = snapshot.GetLineFromLineNumber(cursorLine);
        var lineLength = snapshotLine.End - snapshotLine.Start;
        var stepRange = new LspRange(
            new Position(cursorLine, s.StepTextStartColumn),
            new Position(cursorLine, lineLength));

        _logger.LogVerbose(
            $"CompletionHandler: {result.Entries.Count} step completion(s) for {uri}");
        return new CompletionList(ToItems(result.Entries, stepRange));
    }

    // ── Gherkin keyword completion ────────────────────────────────────────────

    private async Task<CompletionList> HandleKeywordAsync(
        KeywordCompletionContext k,
        DocumentUri uri,
        int cursorLine,
        int cursorChar,
        IGherkinTextSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // Replacement range: first non-whitespace → end of current word + trailing whitespace
        var lineText = snapshot.GetLineFromLineNumber(cursorLine).GetText();

        // A partial table row like "|4" causes the Gherkin AST to fall through to keyword
        // suggestions (including @tags, block keywords), which mangle the row when Tab accepts
        // the top completion. Suppress keyword completions for table rows entirely.
        if (lineText.TrimStart().StartsWith("|", StringComparison.Ordinal))
        {
            _logger.LogVerbose("CompletionHandler: table row — suppressing keyword completions");
            return BuildTableRowSuppressionResult(cursorLine, cursorChar);
        }

        var kwStart = 0;
        while (kwStart < lineText.Length && char.IsWhiteSpace(lineText[kwStart]))
            kwStart++;

        // Issue #561: the range used to extend to the end of the trimmed line (including
        // trailing whitespace), so accepting a keyword deleted everything else on the line --
        // e.g. a scenario title already typed after the keyword. Clamping the end to the cursor
        // position means the replacement can only ever touch text the user has already typed to
        // the LEFT of the caret, never anything to the right of it. Clamping the start down to
        // the (possibly earlier) cursor position too guards against a reversed range in case
        // cursorChar ever lands before the first non-whitespace column.
        var kwRangeEnd = cursorChar;
        var kwRangeStart = Math.Min(kwStart, kwRangeEnd);

        var typedFromLineStart = lineText.Substring(kwRangeStart, kwRangeEnd - kwRangeStart);

        // ── Tag completion (issue #828) ─────────────────────────────────────────
        // Offered whenever the parser's per-line state says TagLine is legal at this position —
        // a tag line in any state, first tag or a second one after a completed tag — and the
        // word being typed at the caret (the text after the last whitespace on the line) is
        // empty or starts with "@". It is deliberately never offered anywhere else: non-tag
        // positions do not advertise TagLine among their expected tokens (titles past their
        // keyword, step text, table rows, etc. are all gated upstream by the context resolver
        // or by the group narrowing below).
        //
        // The tag entries get their own replacement range, spanning only the in-progress tag
        // (last whitespace → caret), not the whole leading prefix: accepting a suggested tag on
        // a line that already carries a completed tag (e.g. "@smoke @") must keep the first tag
        // intact instead of deleting it — the keyword range would swallow it.
        var tagEntries = new List<CompletionEntry>();
        var tagRange = new LspRange(
            new Position(cursorLine, kwRangeStart),
            new Position(cursorLine, kwRangeEnd));

        if (k.ExpectedTokens.Contains(TokenType.TagLine))
        {
            var cursorClamped = Math.Min(cursorChar, lineText.Length);
            var upToCursor = lineText.Substring(0, cursorClamped);
            var inProgressStart = upToCursor.LastIndexOfAny(new[] { ' ', '\t' }) + 1;
            var inProgress = upToCursor.Substring(inProgressStart);

            if (inProgress.Length == 0 || inProgress.StartsWith("@", StringComparison.Ordinal))
            {
                var typedAfterAt = inProgress.StartsWith("@", StringComparison.Ordinal)
                    ? inProgress.Substring(1)
                    : string.Empty;

                var projectTags = await _tagIndex
                    .GetTagCandidatesAsync(uri, cancellationToken)
                    .ConfigureAwait(false);

                tagEntries = _completionService
                    .GetTagCompletions(projectTags, ExtractAtTokens(lineText), typedAfterAt, _matcher)
                    .Entries
                    .ToList();

                tagRange = new LspRange(
                    new Position(cursorLine, inProgressStart),
                    new Position(cursorLine, kwRangeEnd));
            }
        }

        // ── Keyword groups (issue #818 group narrowing, minus the tag group) ─────
        // Keyword completion bundles several *groups* together -- block keywords, step
        // keywords, table/doc-string separators (AddKeywordEntries, one group per TokenType) --
        // and every group is only valid while the text already typed at the start of the line
        // could still become one of ITS members. Narrowing is by whole group, not by individual
        // entry: typing "Gi" still offers Given *and* When *and* Then together (deciding which
        // one is the client's own job, via its list filtering) because the step-keyword group
        // has a match, but typing "@" drops the block-keyword and step-keyword groups entirely,
        // since none of their members could ever start with "@". The TagLine group is excluded
        // here entirely — the tag entries above replace it.
        List<CompletionEntry> kwEntries;
        if (k.ExpectedTokens.Length == 0)
        {
            // Unparsed-document fallback: the permissive default keyword list, flat prefix
            // filter. Tags never appear here — the default list has never contained them and an
            // unparsed document cannot prove the position is tag-legal.
            var fallback = _completionService.GetDefaultKeywordCompletions(k.Dialect).Entries;
            kwEntries = typedFromLineStart.Length == 0
                ? fallback.ToList()
                : fallback
                    .Where(e => e.Label.StartsWith(typedFromLineStart, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
        else if (k.ExpectedTokens.Count(t => t != TokenType.TagLine) > 0)
        {
            var nonTagTokens = k.ExpectedTokens.Where(t => t != TokenType.TagLine).ToArray();
            kwEntries = typedFromLineStart.Length == 0
                ? _completionService.GetKeywordCompletions(nonTagTokens, k.Dialect).Entries.ToList()
                : FilterToMatchingGroups(k, typedFromLineStart, nonTagTokens);
        }
        else
        {
            kwEntries = new List<CompletionEntry>();
        }

        var kwRange = new LspRange(
            new Position(cursorLine, kwRangeStart),
            new Position(cursorLine, kwRangeEnd));

        var items = new List<CompletionItem>(kwEntries.Count + tagEntries.Count);
        items.AddRange(ToItems(kwEntries, kwRange));
        items.AddRange(ToItems(tagEntries, tagRange));

        if (items.Count == 0)
        {
            _logger.LogVerbose(
                $"CompletionHandler: '{typedFromLineStart}' matches no keyword/tag candidate — suppressing");
            return new CompletionList();
        }

        _logger.LogVerbose(
            $"CompletionHandler: {items.Count} keyword completion(s)");
        return new CompletionList(items);
    }

    /// <summary>
    /// Narrows the non-tag keyword groups (one per <see cref="TokenType"/>, e.g. every step
    /// keyword together, or every block keyword together) to those that have at least one
    /// member starting with <paramref name="typedSoFar"/>. A group that matches is kept whole,
    /// not trimmed to just its matching member(s) -- see the call site for why.
    /// <paramref name="tokens"/> must exclude TagLine: the tag group is assembled separately by
    /// the caller (issue #828), with its own per-word replacement range.
    /// </summary>
    private List<CompletionEntry> FilterToMatchingGroups(
        KeywordCompletionContext k,
        string typedSoFar,
        TokenType[] tokens)
    {
        var entries = new List<CompletionEntry>();
        foreach (var token in tokens)
        {
            var group = _completionService.GetKeywordCompletions(new[] { token }, k.Dialect).Entries;
            if (group.Any(e => e.Label.StartsWith(typedSoFar, StringComparison.OrdinalIgnoreCase)))
                entries.AddRange(group);
        }
        return entries;
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
                Label = e.Label,
                Detail = e.Detail,
                Kind = (CompletionItemKind)(int)e.Kind,
                SortText = e.SortText,
                FilterText = e.FilterText,
                TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit
                {
                    Range = range,
                    NewText = e.InsertText ?? e.Label
                })
            })
            .ToList();

    private static bool IsFeatureFile(DocumentUri uri) =>
        uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every whitespace-delimited <c>@</c>-prefixed token on the given line — the tags already typed there, which must not be re-offered as completion candidates.</summary>
    private static IReadOnlyCollection<string> ExtractAtTokens(string lineText)
    {
        var tokens = new List<string>();
        foreach (var token in lineText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("@", StringComparison.Ordinal))
                tokens.Add(token);
        return tokens;
    }
}
