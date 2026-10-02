#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Builds "Insert '&lt;keyword&gt;'" quick fixes for a Gherkin <c>reqnroll.parser</c> diagnostic
/// (issue #563). Reuses <see cref="ICompletionService.GetKeywordCompletions"/> — the same
/// expected-token/dialect resolution keyword completion already performs — since the parser
/// error's <c>expected: #TagLine, #RuleLine, ...</c> message names exactly the token types that
/// would have been legal at that position.
/// </summary>
internal sealed class ParserErrorActionBuilder
{
    private readonly ICompletionService     _completionService;
    private readonly IErrorTelemetryService _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="ParserErrorActionBuilder"/> class.</summary>
    public ParserErrorActionBuilder(ICompletionService completionService, IErrorTelemetryService telemetryService)
    {
        _completionService = completionService;
        _telemetryService  = telemetryService;
    }

    /// <summary>
    /// Builds one "Insert '&lt;keyword&gt;'" action per keyword that would have been legal at
    /// <paramref name="errorTag"/>'s position, or an empty list when the document failed to parse
    /// far enough to recover a dialect/per-line parser state (see
    /// <see cref="IdeSupportGherkinDocument.GetExpectedTokens"/>).
    /// </summary>
    public List<CommandOrCodeAction> Build(DocumentUri uri, IdeSupportTag errorTag, IdeSupportGherkinDocument? gherkinDoc)
    {
        if (gherkinDoc is null)
            return new List<CommandOrCodeAction>();

        var line   = errorTag.Range.StartLinePosition.Line;
        var tokens = gherkinDoc.GetExpectedTokens(line, _telemetryService);
        if (tokens.Length == 0)
            return new List<CommandOrCodeAction>();

        var entries = _completionService.GetKeywordCompletions(tokens, gherkinDoc.GherkinDialect).Entries;
        if (entries.Count == 0)
            return new List<CommandOrCodeAction>();

        var diagnostic = DiagnosticsPublishHandler.ToLspDiagnostic(new GherkinDiagnostic(
            errorTag.Data as string ?? "Gherkin parse error.",
            errorTag.Range,
            GherkinDiagnosticSeverity.Error,
            DiagnosticsAggregator.ParserSource));
        var diagnostics = new Container<Diagnostic>(diagnostic);

        // Replace the diagnostic's whole flagged span (the same range the squiggle covers) rather
        // than inserting at its start — matching how keyword *completion* already replaces the
        // partial word being typed (CompletionHandler.HandleKeyword's kwRange). Splicing the fix
        // in before the bad text instead — the original behaviour — left the bad text in place:
        // e.g. a "Th" typo squiggle offering "Insert '\"\"\"'" produced the malformed `"""Th`
        // rather than replacing "Th" outright (confirmed live in VS, issue #563 follow-up).
        var replaceRange = errorTag.Range.ToLspRange();

        // Mark the entry closest to what the user actually typed as preferred, so VS (which
        // relies on IsPreferred rather than array order to pick the lightbulb's primary "Fix" —
        // see DefineStepsActionBuilder) promotes it instead of bucketing every option under
        // "Other Fixes" with no default. The comparison is purely textual against whatever label
        // GetKeywordCompletions already resolved for this document's dialect — never a hard-coded
        // keyword list, so it works the same for a French/German/etc. feature file.
        var preferredIndex = FindPreferredEntryIndex(GetFlaggedText(errorTag.Range), entries);

        return entries.Select((entry, index) => new CommandOrCodeAction(new CodeAction
        {
            Title       = $"Insert '{entry.Label.Trim()}'",
            Kind        = CodeActionKind.QuickFix,
            Diagnostics = diagnostics,
            IsPreferred = index == preferredIndex,
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = null },
                        Edits = new TextEditContainer(new TextEdit
                        {
                            Range   = replaceRange,
                            NewText = entry.InsertText ?? entry.Label
                        })
                    }))
            }
        })).ToList();
    }

    /// <summary>Extracts the literal text the diagnostic's range spans, e.g. the "Th" of a "Th" typo.</summary>
    private static string GetFlaggedText(GherkinRange range)
    {
        var text  = range.Snapshot.GetText();
        var start = Math.Clamp(range.Start, 0, text.Length);
        var end   = Math.Clamp(range.End, start, text.Length);
        return text[start..end].Trim();
    }

    /// <summary>
    /// Finds the entry whose label shares the longest case-insensitive leading prefix with
    /// <paramref name="flaggedText"/> — e.g. "Th" and "Then" share "Th" (length 2), while "Th" and
    /// a table/docstring separator share nothing (length 0). Returns -1 (no preferred entry) when
    /// nothing shares a prefix, or more than one entry ties for the longest one — an arbitrary pick
    /// would be worse than the pre-existing "all equal" presentation.
    /// </summary>
    private static int FindPreferredEntryIndex(string flaggedText, IReadOnlyList<CompletionEntry> entries)
    {
        if (flaggedText.Length == 0)
            return -1;

        var bestIndex  = -1;
        var bestLength = 0;
        var tied       = false;

        for (var i = 0; i < entries.Count; i++)
        {
            var length = CommonPrefixLength(flaggedText, entries[i].Label.Trim());
            if (length == 0) continue;

            if (length > bestLength)
            {
                bestLength = length;
                bestIndex  = i;
                tied       = false;
            }
            else if (length == bestLength)
            {
                tied = true;
            }
        }

        return tied ? -1 : bestIndex;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i   = 0;
        while (i < max && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i]))
            i++;
        return i;
    }
}
