using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <inheritdoc cref="IDiagnosticsAggregator"/>
public sealed class DiagnosticsAggregator : IDiagnosticsAggregator
{
    /// <summary>LSP <c>source</c> field value for Gherkin parse-error diagnostics.</summary>
    public const string ParserSource  = "reqnroll.parser";

    /// <summary>LSP <c>source</c> field value for undefined-step/binding diagnostics.</summary>
    public const string BindingSource = "reqnroll.binding";

    /// <summary>Hover message shown for every unmatched step.</summary>
    public const string UndefinedStepMessage = "Step definition not found.";

    /// <summary>Fallback message for an ambiguous step when no combined error message was produced.</summary>
    public const string AmbiguousStepMessage = "Ambiguous step definition.";

    /// <inheritdoc/>
    public IReadOnlyList<GherkinDiagnostic> Aggregate(
        IReadOnlyCollection<IdeSupportTag> tags,
        FeatureBindingMatchSet matchSet)
    {
        var diagnostics = new List<GherkinDiagnostic>();

        // Parser-error diagnostics: each is stored as a IdeSupportTag of type ParserError whose
        // Data holds the parser exception message string.
        foreach (var tag in tags)
        {
            if (tag.Type != IdeSupportTagTypes.ParserError)
                continue;

            var message = tag.Data as string ?? "Gherkin parse error.";
            diagnostics.Add(new GherkinDiagnostic(
                message,
                tag.Range,
                GherkinDiagnosticSeverity.Error,
                ParserSource));
        }

        // Undefined-step/binding diagnostics: undefined steps from the binding match set.
        // Issue #514's "cheap first step": when the step structurally matches an *invalid*
        // binding (e.g. a step-definition method missing a required `static`), Result.GetErrorMessage()
        // carries that binding's Error (see ProjectBindingRegistry.FindNearMissErrors) instead of
        // being null — the same mechanism the Ambiguous case below already uses, so this just
        // stops discarding it, per the issue's own recommendation.
        foreach (var step in matchSet.Undefined)
        {
            diagnostics.Add(new GherkinDiagnostic(
                step.Result.GetErrorMessage() ?? UndefinedStepMessage,
                step.Range,
                GherkinDiagnosticSeverity.Warning,
                BindingSource));
        }

        // Ambiguous matches reported as errors. Result.GetErrorMessage() lists every matching
        // binding (MatchResult.CreateMultiMatch builds it for exactly this case), so hovering
        // shows which step definitions collide instead of just "it's ambiguous".
        foreach (var step in matchSet.Ambiguous)
        {
            diagnostics.Add(new GherkinDiagnostic(
                step.Result.GetErrorMessage() ?? AmbiguousStepMessage,
                step.Range,
                GherkinDiagnosticSeverity.Error,
                BindingSource));
        }

        return diagnostics;
    }
}
