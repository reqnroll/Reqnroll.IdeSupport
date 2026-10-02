#nullable enable

using Gherkin.Ast;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>
/// Classifies what a Run lens lookup was for (the <c>Kind</c> telemetry property, issue #849) from the
/// document's own tags, mirroring how <c>ScenarioTestTargetResolver</c> picks its scenario/row — so the
/// event says what the user clicked, not how many methods the generated code-behind happened to have.
/// </summary>
internal static class TestTargetTelemetry
{
    /// <summary>
    /// <c>ExampleRow</c> when <paramref name="range"/> lands on an Examples body row, else <c>Outline</c> or
    /// <c>Scenario</c> by the scenario it intersects; <see langword="null"/> when it intersects no scenario.
    /// </summary>
    public static string? ClassifyScenario(IReadOnlyCollection<IdeSupportTag> tags, GherkinRange range)
    {
        var oneBasedLine = range.StartLinePosition.Line + 1;
        var examples = tags.FirstOrDefault(t =>
            t.Type == IdeSupportTagTypes.ExamplesBlock && t.Range.IntersectsWith(range))?.Data as Examples;
        if (examples?.TableBody?.Any(r => r.Location.Line == oneBasedLine) == true)
            return TelemetryProperties.TestTargetKind.ExampleRow;

        var scenario = tags.FirstOrDefault(t =>
            t.Type == IdeSupportTagTypes.ScenarioDefinitionBlock && t.Range.IntersectsWith(range));
        return scenario?.Data switch
        {
            ScenarioOutline => TelemetryProperties.TestTargetKind.Outline,
            Scenario => TelemetryProperties.TestTargetKind.Scenario,
            _ => null,
        };
    }

    /// <summary>
    /// <c>Rule</c> when a <c>Rule:</c> block encloses <paramref name="containerRange"/>, otherwise <c>Feature</c>
    /// (the request carries only a range; a Feature's range is never inside a Rule's).
    /// </summary>
    public static string ClassifyContainer(IReadOnlyCollection<IdeSupportTag> tags, GherkinRange containerRange) =>
        tags.Any(t => t.Type == IdeSupportTagTypes.RuleBlock
                      && t.Range.Start <= containerRange.Start && t.Range.End >= containerRange.End)
            ? TelemetryProperties.TestTargetKind.Rule
            : TelemetryProperties.TestTargetKind.Feature;
}
