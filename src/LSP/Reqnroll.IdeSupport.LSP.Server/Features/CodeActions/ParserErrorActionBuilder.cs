#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
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

        // Insert (never replace) at the exact position the parser stopped — the diagnostic's own
        // start point — rather than presuming how to rewrite whatever the user already typed.
        var insertPosition = errorTag.Range.ToLspRange().Start;
        var insertRange     = new LspRange(insertPosition, insertPosition);

        return entries.Select(entry => new CommandOrCodeAction(new CodeAction
        {
            Title       = $"Insert '{entry.Label.Trim()}'",
            Kind        = CodeActionKind.QuickFix,
            Diagnostics = diagnostics,
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = null },
                        Edits = new TextEditContainer(new TextEdit
                        {
                            Range   = insertRange,
                            NewText = entry.InsertText ?? entry.Label
                        })
                    }))
            }
        })).ToList();
    }
}
