#nullable enable

using System.IO;
using Gherkin.Ast;


using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Bindings;



namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// The immutable set of step binding matches for one feature document, cached against
/// <c>(DocumentId, Owner, DocumentVersion, RegistryVersion)</c>. This is the value stored by
/// <see cref="IBindingMatchService"/> and queried by Go to Step Definition, the diagnostics
/// aggregator (undefined-step/binding diagnostics) and find-usages / code-lens usage counts.
/// </summary>
public sealed class FeatureBindingMatchSet
{
    /// <summary>An empty match set with no steps, keyed to an unknown document and project owner.</summary>
    public static readonly FeatureBindingMatchSet Empty =
        new(string.Empty, ProjectOwner.Unknown, null, 0, Array.Empty<StepBindingMatch>());

    /// <summary>Initializes a new instance of the <see cref="FeatureBindingMatchSet"/> class.</summary>
    /// <param name="scenarios">
    /// Every Scenario/Scenario Outline definition in the document (issue #373's hook-match-count
    /// CodeLens). Optional and defaults to empty so existing callers (tests, in particular) that
    /// construct a set directly rather than via <see cref="FromTags"/> don't need updating.
    /// </param>
    public FeatureBindingMatchSet(
        string documentId,
        ProjectOwner owner,
        int? documentVersion,
        int registryVersion,
        IReadOnlyList<StepBindingMatch> steps,
        IReadOnlyList<FeatureScenarioInfo>? scenarios = null)
    {
        Key = new MatchSetKey(
            documentId ?? throw new ArgumentNullException(nameof(documentId)),
            owner.IsKnown ? owner : ProjectOwner.Unknown);
        DocumentVersion = documentVersion;
        RegistryVersion = registryVersion;
        Steps           = steps ?? throw new ArgumentNullException(nameof(steps));
        Scenarios       = scenarios ?? Array.Empty<FeatureScenarioInfo>();
    }

    /// <summary>The composite cache key: document URI + owning project.</summary>
    public MatchSetKey Key { get; }

    /// <summary>The feature document URI (derived from <see cref="Key"/>).</summary>
    public string DocumentId => Key.DocumentId;

    /// <summary>The owning project for this match set (derived from <see cref="Key"/>).</summary>
    public ProjectOwner Owner => Key.Owner;

    /// <summary>The feature document version these matches were computed for, when known.</summary>
    public int? DocumentVersion { get; }

    /// <summary>The <see cref="ProjectBindingRegistry"/> version these matches were computed against.</summary>
    public int RegistryVersion { get; }

    /// <summary>Gets every step binding match in this feature document.</summary>
    public IReadOnlyList<StepBindingMatch> Steps { get; }

    /// <summary>
    /// Gets every Scenario/Scenario Outline definition in this feature document (issue #373's
    /// hook-match-count CodeLens; excludes Background blocks). Populated regardless of whether
    /// the document is currently open, since this is the same workspace-wide cache
    /// <c>GherkinDocumentTaggerService</c> populates for closed files.
    /// </summary>
    public IReadOnlyList<FeatureScenarioInfo> Scenarios { get; }

    /// <summary>Gets the steps with no matching binding.</summary>
    public IEnumerable<StepBindingMatch> Undefined => Steps.Where(s => s.IsUndefined);
    /// <summary>Gets the steps with exactly one matching binding.</summary>
    public IEnumerable<StepBindingMatch> Defined   => Steps.Where(s => s.IsDefined);
    /// <summary>Gets the steps that match more than one binding.</summary>
    public IEnumerable<StepBindingMatch> Ambiguous => Steps.Where(s => s.IsAmbiguous);

    /// <summary>The step whose text span contains <paramref name="offset"/>, or null. Used by Go to Step Definition.</summary>
    public StepBindingMatch? FindAt(int offset) => Steps.FirstOrDefault(s => s.Contains(offset));

    /// <summary>
    /// Builds a match set from the flattened tag collection produced by <c>IdeSupportTagParser</c>.
    /// Each <see cref="IdeSupportTagTypes.DefinedStep"/> / <see cref="IdeSupportTagTypes.UndefinedStep"/> /
    /// <see cref="IdeSupportTagTypes.AmbiguousStep"/> tag carries the step text span as its range and the
    /// computed <see cref="MatchResult"/> as its data; this method projects those into
    /// <see cref="StepBindingMatch"/> entries.
    /// </summary>
    /// <remarks>
    /// A single step can emit both a DefinedStep and an UndefinedStep tag (e.g. a scenario outline
    /// whose example rows partly match), but both reference the same <see cref="MatchResult"/> at the
    /// same span; such duplicates are collapsed so the set holds one entry per step.
    /// </remarks>
    public static FeatureBindingMatchSet FromTags(
        string documentId,
        int? documentVersion,
        int registryVersion,
        IEnumerable<IdeSupportTag> tags,
        ProjectOwner owner = default)
    {
        var tagList = tags as IReadOnlyCollection<IdeSupportTag> ?? tags.ToList();
        var byStart = new Dictionary<int, StepBindingMatch>();

        // Short project name for the Project column (e.g. "Minimal", "Minimalnet481").
        var projectName = owner.IsKnown
            ? Path.GetFileNameWithoutExtension(owner.ProjectFile)
            : null;

        foreach (var tag in tagList)
        {
            if (tag.Type is not (IdeSupportTagTypes.DefinedStep or IdeSupportTagTypes.UndefinedStep or IdeSupportTagTypes.AmbiguousStep))
                continue;
            if (tag.Data is not MatchResult match)
                continue;

            // Collapse the DefinedStep/UndefinedStep pair a single step may emit: same span, same result.
            if (!byStart.ContainsKey(tag.Range.Start))
            {
                // Walk up the tag hierarchy to extract keyword and scenario name at parse time
                // so the handler does not have to re-derive them from snapshot text later.
                //   DefinedStep.ParentTag  = StepBlock       (Data = Gherkin.Ast.Step)
                //   StepBlock.ParentTag    = ScenarioDefinitionBlock (Data = IHasDescription)
                var stepBlockTag      = tag.ParentTag;
                var scenarioDefTag    = stepBlockTag?.ParentTag;
                var keyword           = (stepBlockTag?.Data as Step)?.Keyword?.Trim();
                var scenarioName      = (scenarioDefTag?.Data as IHasDescription)?.Name;
                if (string.IsNullOrEmpty(scenarioName)) scenarioName = null;

                // Walk up looking for an enclosing Rule block, then continue to the feature block
                // to extract the feature title at parse time. Scenarios nested under a Rule: block
                // have an extra RuleBlock tag between the ScenarioDefinitionBlock and the
                // FeatureBlock, so keep climbing past it (and any other intermediate tags) until we
                // reach the FeatureBlock rather than assuming exactly one level up.
                var ruleTag = scenarioDefTag?.ParentTag;
                while (ruleTag != null && ruleTag.Type is not (IdeSupportTagTypes.RuleBlock or IdeSupportTagTypes.FeatureBlock))
                    ruleTag = ruleTag.ParentTag;
                var ruleName = ruleTag?.Type == IdeSupportTagTypes.RuleBlock ? (ruleTag.Data as IHasDescription)?.Name : null;
                if (string.IsNullOrEmpty(ruleName)) ruleName = null;

                var featureTag = ruleTag;
                while (featureTag != null && featureTag.Type != IdeSupportTagTypes.FeatureBlock)
                    featureTag = featureTag.ParentTag;
                var featureName       = (featureTag?.Data as Feature)?.Name;
                if (string.IsNullOrEmpty(featureName)) featureName = null;

                byStart[tag.Range.Start] = new StepBindingMatch(
                    documentId, tag.Range, match, keyword, scenarioName, projectName, featureName, ruleName);
            }
        }

        var steps = byStart.Values.OrderBy(s => s.Range.Start).ToArray();

        // Background shares ScenarioDefinitionBlock with real scenarios but isn't independently
        // executed/countable, so it's excluded here (issue #373).
        var scenarios = tagList
            .Where(t => t.Type == IdeSupportTagTypes.ScenarioDefinitionBlock)
            .Where(t => !((IGherkinDocumentContext)t).IsBackground())
            .Select(t => new FeatureScenarioInfo(documentId, t))
            .OrderBy(s => s.Range.Start)
            .ToArray();

        return new FeatureBindingMatchSet(documentId, owner, documentVersion, registryVersion, steps, scenarios);
    }
}
