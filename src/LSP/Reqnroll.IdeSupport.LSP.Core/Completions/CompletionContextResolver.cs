using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;

using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;





namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// Walks the parsed Deveroom tag tree for a snapshot to decide whether the cursor calls for
/// Gherkin keyword completion or step-definition sample completion, and packages all the
/// Gherkin-specific data the handler needs to fulfil the request.
/// </summary>
public sealed class CompletionContextResolver : ICompletionContextResolver
{
    private readonly IIdeSupportTagParser _tagParser;
    private readonly IErrorTelemetryService _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="CompletionContextResolver"/> class.</summary>
    public CompletionContextResolver(IIdeSupportTagParser tagParser, IErrorTelemetryService telemetryService)
    {
        _tagParser         = tagParser;
        _telemetryService = telemetryService;
    }

    /// <summary>Determines whether the cursor calls for keyword or step-definition completion and resolves the matching completion context, or <see langword="null"/> if neither applies.</summary>
    public CompletionContext? Resolve(
        IGherkinTextSnapshot   snapshot,
        int                    cursorLine,
        int                    cursorChar,
        ProjectBindingRegistry registry,
        string                 fallbackLanguage)
    {
        var tags = _tagParser.Parse(snapshot, registry);

        var docTag     = tags.FirstOrDefault(t => t.Type == IdeSupportTagTypes.Document);
        var gherkinDoc = docTag?.Data as IdeSupportGherkinDocument;
        var dialect    = gherkinDoc?.GherkinDialect
                      ?? new GherkinDialectProvider(fallbackLanguage).DefaultDialect;

        // ── Step-definition-sample completion ───────────────────────────────────
        // Cursor must be on a recognised step line and at or past the step text start
        // (i.e. after the keyword and its trailing space).
        var stepTag = tags.FirstOrDefault(t =>
            t.Type == IdeSupportTagTypes.StepBlock &&
            t.Data is IdeSupportGherkinStep s &&
            s.Location.Line - 1 == cursorLine);   // Gherkin lines are 1-based

        if (stepTag?.Data is IdeSupportGherkinStep cursorStep)
        {
            var snapshotLine = snapshot.GetLineFromLineNumber(cursorLine);
            var cursorOffset = snapshotLine.Start + cursorChar;

            // Gherkin Location.Column is 1-based; Keyword includes the trailing space.
            var stepTextStart = snapshotLine.Start
                              + (cursorStep.Location.Column - 1)
                              + cursorStep.Keyword.Length;

            if (cursorOffset >= stepTextStart)
            {
                var typed = snapshot.GetText()
                    .Substring(stepTextStart, cursorOffset - stepTextStart);
                var stepTextStartColumn = stepTextStart - snapshotLine.Start;

                return new StepCompletionContext(cursorStep, typed, stepTextStartColumn);
            }
        }

        // ── Gherkin keyword completion ──────────────────────────────────────────
        var tokens = gherkinDoc?.GetExpectedTokens(cursorLine, _telemetryService)
                     ?? Array.Empty<TokenType>();

        return new KeywordCompletionContext(dialect, tokens);
    }
}
