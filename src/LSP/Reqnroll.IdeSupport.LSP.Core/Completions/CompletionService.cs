using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;




namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// VS-free, netstandard2.0-compatible implementation of <see cref="ICompletionService"/>.
/// Ports keyword-completion logic from the legacy <c>DeveroomCompletionSource</c> and
/// step-completion logic that uses <see cref="StepDefinitionSampler"/>.
/// </summary>
public sealed class CompletionService : ICompletionService
{
    // ── Gherkin keyword completion ──────────────────────────────────────────────

    /// <summary>Builds keyword completion entries for the given expected token types, in the given dialect.</summary>
    public CompletionResult GetKeywordCompletions(TokenType[] expectedTokens, GherkinDialect dialect)
    {
        var entries = new List<CompletionEntry>();
        foreach (var token in expectedTokens)
            AddKeywordEntries(entries, token, dialect);
        return new CompletionResult(entries);
    }

    /// <summary>Builds the permissive fallback keyword completion list (all step and block keywords) used when the document could not be parsed.</summary>
    public CompletionResult GetDefaultKeywordCompletions(GherkinDialect dialect)
    {
        var entries = new List<CompletionEntry>();

        // All step keywords (bullet included — default fallback is permissive)
        foreach (var kw in AllStepKeywords(dialect))
            entries.Add(Kw(kw, "Step keyword"));

        // All block keywords with ": " postfix
        foreach (var kw in AllBlockKeywords(dialect))
            entries.Add(Kw(kw + ": ", "Block keyword"));

        return new CompletionResult(entries);
    }

    private static void AddKeywordEntries(
        List<CompletionEntry> entries,
        TokenType             token,
        GherkinDialect        dialect)
    {
        switch (token)
        {
            case TokenType.FeatureLine:
                AddBlock(entries, dialect.FeatureKeywords,
                    "Introduces the feature being described");
                break;
            case TokenType.RuleLine:
                AddBlock(entries, dialect.RuleKeywords,
                    "Describes a business rule illustrated by the subsequent scenarios");
                break;
            case TokenType.BackgroundLine:
                AddBlock(entries, dialect.BackgroundKeywords,
                    "Describes context common to all scenarios in this feature file");
                break;
            case TokenType.ScenarioLine:
                AddBlock(entries, dialect.ScenarioKeywords,
                    "Illustrates a single system behaviour");
                AddBlock(entries, dialect.ScenarioOutlineKeywords,
                    "A template for generating several, similar scenarios");
                break;
            case TokenType.ExamplesLine:
                AddBlock(entries, dialect.ExamplesKeywords,
                    "A table of data used in conjunction with a scenario outline");
                break;
            case TokenType.StepLine:
                AddStep(entries, RemoveBullet(dialect.GivenStepKeywords),
                    "Describes the context for the behaviour");
                AddStep(entries, RemoveBullet(dialect.WhenStepKeywords),
                    "Describes the action that initiates the behaviour");
                AddStep(entries, RemoveBullet(dialect.ThenStepKeywords),
                    "Describes the expected outcome");
                AddStep(entries, dialect.AndStepKeywords,
                    "Used to combine steps in a readable format");
                AddStep(entries, RemoveBullet(dialect.ButStepKeywords),
                    "Used to combine steps in a readable format");
                break;
            case TokenType.DocStringSeparator:
                entries.Add(Kw("\"\"\"", "Doc-string separator: Provides multi-line text parameter for the step"));
                entries.Add(Kw("```",   "Doc-string separator: Provides multi-line text parameter for the step"));
                break;
            case TokenType.TableRow:
                entries.Add(Kw("| ", "Data table and examples table cell separator"));
                break;
            case TokenType.Language:
                entries.Add(Kw("#language: ", "Specifies the language of the feature file"));
                break;
            case TokenType.TagLine:
                // A bare "@" tag-prefix entry. Real tag completions (built-in @ignore plus the
                // tags already used across the project) are served by GetTagCompletions — this
                // placeholder remains only as the ParserErrorActionBuilder quick-fix candidate
                // ("Insert '@'") for a position where Gherkin expected a tag (issue #828).
                entries.Add(Kw("@", "Tag prefix — completes to a tag used in the project or the built-in @ignore"));
                break;
        }
    }

    // ── Step-definition-sample completion ───────────────────────────────────────

    /// <summary>Builds ranked step-definition-sample completion entries matching the step's <c>ScenarioBlock</c> and the text typed so far.</summary>
    public CompletionResult GetStepCompletions(
        IdeSupportGherkinStep                     step,
        string                                  typedAfterKeyword,
        ProjectBindingRegistry                  registry,
        Func<ProjectStepDefinitionBinding, int> usageCounter,
        ICompletionMatcher                      matcher)
    {
        if (registry == ProjectBindingRegistry.Invalid)
            return CompletionResult.Empty;

        var sampler = new StepDefinitionSampler();
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<StepCandidate>();

        foreach (var sd in registry.StepDefinitions)
        {
            if (!sd.IsValid || sd.StepDefinitionType != step.ScenarioBlock)
                continue;

            // Method-name-style bindings (no explicit attribute expression, e.g. [Given] public
            // void The_First_Number_Is_P0(int p)) have no authored step text to offer as a
            // completion sample — their auto-generated regex isn't valid Gherkin step text, so
            // inserting it would corrupt the .feature file rather than help the user (issue #344).
            if (sd.SpecifiedExpression is null)
                continue;

            var sample = sampler.GetStepDefinitionSample(sd);
            if (!seen.Add(sample))
                continue;

            candidates.Add(new StepCandidate(sample, usageCounter(sd)));
        }

        var ranked  = matcher.Rank(typedAfterKeyword, candidates);
        var entries = ranked
            .Select((sc, i) => new CompletionEntry(
                Label:      sc.Sample,
                Detail:     null,
                Kind:       CompletionEntryKind.Text,
                InsertText: sc.Sample,
                FilterText: sc.Sample,
                SortText:   i.ToString("D6")))
            .ToList();

        return new CompletionResult(entries, matcher.IsIncomplete);
    }

    // ── Tag completion ────────────────────────────────────────────────────────

    private const string IgnoreTagDetail =
        "Excludes the tagged feature/scenario from the test run (built-in Reqnroll tag)";

    /// <summary>Builds tag completion entries: the built-in tags plus the tags already used across the project, minus the tags already typed on the completing line, ranked by usage count.</summary>
    public CompletionResult GetTagCompletions(
        IReadOnlyCollection<StepCandidate> projectTags,
        IReadOnlyCollection<string> tagsOnTheLine,
        string typedAfterAt,
        ICompletionMatcher matcher)
    {
        // Start from the built-in set so a project that has never used any tag still gets
        // candidates; a project tag with the same name as a built-in keeps its own usage count
        // so ranking below reflects real usage.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [BuiltInTagNames.Ignore] = 0
        };

        foreach (var tag in projectTags)
        {
            counts[tag.Sample] = counts.TryGetValue(tag.Sample, out var existing)
                ? existing + tag.UsageCount
                : tag.UsageCount;
        }

        foreach (var usedTag in tagsOnTheLine)
            counts.Remove(usedTag);

        if (counts.Count == 0)
            return CompletionResult.Empty;

        // Most-used first (ties broken ordinally for a stable list); the matcher may then re-rank
        // or trim server-side (the FuzzySharp contingency) just like step samples.
        var candidates = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new StepCandidate(kv.Key, kv.Value))
            .ToList();

        var ranked = matcher.Rank(typedAfterAt, candidates);
        var entries = ranked
            .Select((sc, i) => new CompletionEntry(
                Label: sc.Sample,
                Detail: sc.Sample == BuiltInTagNames.Ignore ? IgnoreTagDetail : null,
                Kind: CompletionEntryKind.Keyword,
                InsertText: sc.Sample,
                FilterText: sc.Sample,
                SortText: i.ToString("D6")))
            .ToList();

        return new CompletionResult(entries, matcher.IsIncomplete);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddBlock(List<CompletionEntry> entries, string[] keywords, string detail)
    {
        foreach (var kw in keywords)
            entries.Add(Kw(kw + ": ", detail));
    }

    private static void AddStep(List<CompletionEntry> entries, IEnumerable<string> keywords, string detail)
    {
        foreach (var kw in keywords)
            entries.Add(Kw(kw, detail));
    }

    private static CompletionEntry Kw(string label, string detail)
        => new(Label: label, Detail: detail, Kind: CompletionEntryKind.Keyword);

    private static IEnumerable<string> RemoveBullet(string[] keywords)
        => keywords.Where(k => !k.StartsWith("*", StringComparison.Ordinal));

    private static IEnumerable<string> AllStepKeywords(GherkinDialect dialect)
        => dialect.GivenStepKeywords
                  .Concat(dialect.WhenStepKeywords)
                  .Concat(dialect.ThenStepKeywords)
                  .Concat(dialect.AndStepKeywords)
                  .Concat(dialect.ButStepKeywords)
                  .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> AllBlockKeywords(GherkinDialect dialect)
        => dialect.FeatureKeywords
                  .Concat(dialect.RuleKeywords)
                  .Concat(dialect.BackgroundKeywords)
                  .Concat(dialect.ScenarioKeywords)
                  .Concat(dialect.ScenarioOutlineKeywords)
                  .Concat(dialect.ExamplesKeywords);
}
