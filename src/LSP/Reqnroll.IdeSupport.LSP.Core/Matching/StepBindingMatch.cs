#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;





namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// A single feature-file step's resolved binding match, together with the text span the
/// step occupies in the feature document. This is the per-<c>(featureURI, range)</c>
/// coordinate of the binding match cache described in section 3 of the
/// LSP IDE Support design.
/// </summary>
/// <remarks>
/// Match <em>computation</em> still happens in <c>DeveroomTagParser</c> while it walks the
/// document (it has the snapshot for span math and the tag tree for
/// <c>IGherkinDocumentContext</c>. A <see cref="StepBindingMatch"/> captures the
/// result of that computation so downstream features — Go to Step Definition, diagnostics
/// (undefined-step/binding diagnostics), find usages — can query it without re-parsing.
/// </remarks>
public sealed class StepBindingMatch
{
    /// <summary>Initializes a new instance of the <see cref="StepBindingMatch"/> class.</summary>
    public StepBindingMatch(
        string       featureDocumentId,
        GherkinRange range,
        MatchResult  result,
        string?      keyword      = null,
        string?      scenarioName = null,
        string?      projectName  = null,
        string?      featureName  = null,
        string?      ruleName     = null)
    {
        FeatureDocumentId = featureDocumentId ?? throw new ArgumentNullException(nameof(featureDocumentId));
        Range      = range  ?? throw new ArgumentNullException(nameof(range));
        Result     = result ?? throw new ArgumentNullException(nameof(result));
        Keyword      = keyword;
        ScenarioName = scenarioName;
        ProjectName  = projectName;
        FeatureName  = featureName;
        RuleName     = ruleName;
    }

    /// <summary>
    /// The document ID (URI string) of the feature file that contains this step.
    /// Backs Find Usages and the Code Lens usage counts: callers need the feature file
    /// URI to build <c>Location</c> responses without a separate document-ID lookup.
    /// </summary>
    public string FeatureDocumentId { get; }

    /// <summary>The span of the step text (excluding the keyword) within the feature document.</summary>
    public GherkinRange Range { get; }

    /// <summary>The full match result for the step (Defined / Undefined / Ambiguous, plus errors).</summary>
    public MatchResult Result { get; }

    /// <summary>Gets whether this step has no matching binding.</summary>
    public bool IsUndefined => Result.HasUndefined;
    /// <summary>Gets whether this step matches exactly one binding.</summary>
    public bool IsDefined => Result.HasDefined;
    /// <summary>Gets whether this step matches more than one binding.</summary>
    public bool IsAmbiguous => Result.HasAmbiguous;

    /// <summary>
    /// True when <paramref name="offset"/> (absolute char offset) falls anywhere on the step's
    /// line(s) — not just within the step text span. Gherkin is line-oriented, so a click on the
    /// keyword, leading indentation, trailing whitespace, or just past the last character should
    /// still resolve to the step; this is what lets Go to Step Definition match on the whole line.
    /// </summary>
    public bool Contains(int offset)
    {
        var snapshot = Range.Snapshot;
        var startLine = snapshot.GetLineFromLineNumber(Range.StartLinePosition.Line);
        var endLine = snapshot.GetLineFromLineNumber(Range.EndLinePosition.Line);

        return offset >= startLine.Start && offset <= endLine.End;
    }

    /// <summary>
    /// The Gherkin step keyword as it appears in the feature file, trimmed
    /// (e.g. <c>"Given"</c>, <c>"When"</c>, <c>"Then"</c>, <c>"And"</c>).
    /// <see langword="null"/> when the match was built without AST context.
    /// </summary>
    public string? Keyword { get; }

    /// <summary>
    /// The name of the scenario or scenario outline that contains this step
    /// (e.g. <c>"Add two numbers"</c>).
    /// <see langword="null"/> for Background steps or when AST context was unavailable.
    /// </summary>
    public string? ScenarioName { get; }

    /// <summary>
    /// The short project name derived from <c>ProjectOwner.ProjectFile</c> at cache-build time
    /// (e.g. <c>"Minimal"</c>, <c>"Minimalnet481"</c>).
    /// <see langword="null"/> when the owner was unknown at cache-build time.
    /// </summary>
    public string? ProjectName { get; }

    /// <summary>
    /// The name of the feature that contains this step, as declared by the <c>Feature:</c> line
    /// in the <c>.feature</c> file (e.g. <c>"Calculator"</c>).
    /// <see langword="null"/> when the feature title could not be determined at cache-build time.
    /// </summary>
    public string? FeatureName { get; }

    /// <summary>
    /// The name of the <c>Rule:</c> block that contains the scenario, when the step is nested
    /// under one (e.g. <c>"Discounts apply to members only"</c>).
    /// <see langword="null"/> when the scenario is not inside a Rule block or AST context was
    /// unavailable.
    /// </summary>
    public string? RuleName { get; }

    /// <summary>
    /// The source locations of every binding this step resolves to — one for a unique match,
    /// several for an ambiguous match, none for an undefined step.
    /// </summary>
    public IEnumerable<SourceLocation> BindingLocations =>
        Result.Items
            .Where(i => i.MatchedStepDefinition?.Implementation?.SourceLocation != null)
            .Select(i => i.MatchedStepDefinition.Implementation.SourceLocation!);

    /// <summary>
    /// The stable <see cref="BindingId"/> identity paired with the source location of every
    /// binding this step resolves to (issue #471's reverse index) — one pair per entry in
    /// <see cref="BindingLocations"/>, in the same order.
    /// </summary>
    public IEnumerable<(BindingId Id, SourceLocation Location)> BindingIdentities =>
        Result.Items
            .Where(i => i.MatchedStepDefinition?.Implementation?.SourceLocation != null)
            .Select(i => (BindingId.For(i.MatchedStepDefinition), i.MatchedStepDefinition.Implementation.SourceLocation!));
}
