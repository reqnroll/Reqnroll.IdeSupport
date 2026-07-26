


using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Gherkin.Ast;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <inheritdoc cref="IDeveroomTagParser"/>
public class DeveroomTagParser : IDeveroomTagParser
{
    internal static readonly Regex NewLineRe = new(@"\r\n|\n|\r");
    private readonly IDeveroomConfigurationProvider _deveroomConfigurationProvider;
    private readonly IIdeSupportLogger _logger;
    private readonly IErrorTelemetryService _telemetryService;

    /// <summary>Initializes a new instance of the <see cref="DeveroomTagParser"/> class.</summary>
    public DeveroomTagParser(
        IIdeSupportLogger logger,
        IErrorTelemetryService telemetryService,
        IDeveroomConfigurationProvider deveroomConfigurationProvider
    )
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _deveroomConfigurationProvider = deveroomConfigurationProvider;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<DeveroomTag> Parse(
        IGherkinTextSnapshot fileSnapshot,
        ProjectBindingRegistry bindingRegistry)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        try
        {
            var configuration = _deveroomConfigurationProvider.GetConfiguration();
            return ParseInternal(fileSnapshot, bindingRegistry, configuration);
        }
        catch (Exception ex)
        {
            _logger.LogException(_telemetryService, ex, "Unhandled parsing error");
            return Array.Empty<DeveroomTag>();
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogVerbose(
                $"Parsed buffer v{fileSnapshot.Version} in {stopwatch.ElapsedMilliseconds}ms on thread {Thread.CurrentThread.ManagedThreadId}");
        }
    }

    private IReadOnlyCollection<DeveroomTag> ParseInternal(IGherkinTextSnapshot fileSnapshot,
        ProjectBindingRegistry bindingRegistry,
        DeveroomConfiguration deveroomConfiguration)
    {
        var dialectProvider = ReqnrollGherkinDialectProvider.Get(deveroomConfiguration.DefaultFeatureLanguage);
        var parser = new DeveroomGherkinParser(dialectProvider, _telemetryService);

        parser.ParseAndCollectErrors(fileSnapshot.GetText(), _logger,
            out var gherkinDocument, out var parserErrors);

        ImmutableSortedSet<DeveroomTag>.Builder result =
            ImmutableSortedSet.CreateBuilder(new DeveroomTagPositionComparer());

        if (gherkinDocument != null)
            AddGherkinDocumentTags(fileSnapshot, bindingRegistry, gherkinDocument, result);

        foreach (var parserException in parserErrors)
        {
            var line = GetSnapshotLine(parserException.Location, fileSnapshot);
            var startPoint = GetColumnPoint(line, parserException.Location);
            var span = GherkinRange.FromPoint(fileSnapshot, startPoint, line.End - startPoint);

            var deveroomTag = new DeveroomTag(DeveroomTagTypes.ParserError,
                span, parserException.Message);
            result.Add(deveroomTag);
        }

        return result.ToImmutable();
    }

    private void AddGherkinDocumentTags(IGherkinTextSnapshot fileSnapshot, ProjectBindingRegistry bindingRegistry,
        DeveroomGherkinDocument gherkinDocument, ISet<DeveroomTag> result)
    {
        var documentTag = new DeveroomTag(DeveroomTagTypes.Document,
            new GherkinRange(fileSnapshot, 0, fileSnapshot.Length), gherkinDocument);
        result.Add(documentTag);

        if (gherkinDocument.Feature != null)
        {
            var featureTag = GetFeatureTags(fileSnapshot, bindingRegistry, gherkinDocument.Feature);
            var allTags = GetAllTags(featureTag);
            result.UnionWith(allTags);
        }

        if (gherkinDocument.Comments != null)
            foreach (var comment in gherkinDocument.Comments)
            {
                var deveroomTag = new DeveroomTag(DeveroomTagTypes.Comment,
                    GetTextSpan(fileSnapshot, comment.Location, comment.Text));
                result.Add(deveroomTag);
            }
    }

    private DeveroomTag GetFeatureTags(IGherkinTextSnapshot fileSnapshot, ProjectBindingRegistry bindingRegistry,
        Feature feature)
    {
        var featureTag = CreateDefinitionBlockTag(feature, DeveroomTagTypes.FeatureBlock, fileSnapshot,
            fileSnapshot.LineCount);

        foreach (var block in feature.Children)
            if (block is StepsContainer stepsContainer)
                AddScenarioDefinitionBlockTag(fileSnapshot, bindingRegistry, stepsContainer, featureTag);
            else if (block is Rule rule)
                AddRuleBlockTag(fileSnapshot, bindingRegistry, rule, featureTag);

        return featureTag;
    }

    private void AddRuleBlockTag(IGherkinTextSnapshot fileSnapshot, ProjectBindingRegistry bindingRegistry, Rule rule,
        DeveroomTag featureTag)
    {
        var lastStepsContainer = rule.StepsContainers().LastOrDefault();
        var lastLine = lastStepsContainer != null
            ? GetScenarioDefinitionLastLine(lastStepsContainer)
            : rule.Location.Line;
        var ruleTag = CreateDefinitionBlockTag(rule,
            DeveroomTagTypes.RuleBlock, fileSnapshot,
            lastLine, featureTag);

        foreach (var stepsContainer in rule.StepsContainers())
            AddScenarioDefinitionBlockTag(fileSnapshot, bindingRegistry, stepsContainer, ruleTag);
    }

    private void AddScenarioDefinitionBlockTag(IGherkinTextSnapshot fileSnapshot, ProjectBindingRegistry bindingRegistry,
        StepsContainer scenarioDefinition, DeveroomTag parentTag)
    {
        var scenarioDefinitionTag = CreateDefinitionBlockTag(scenarioDefinition,
            DeveroomTagTypes.ScenarioDefinitionBlock, fileSnapshot,
            GetScenarioDefinitionLastLine(scenarioDefinition), parentTag);

        foreach (var step in scenarioDefinition.Steps)
        {
            var stepTag = scenarioDefinitionTag.AddChild(new DeveroomTag(DeveroomTagTypes.StepBlock,
                GetBlockSpan(fileSnapshot, step.Location, GetStepLastLine(step)), step));

            stepTag.AddChild(
                new DeveroomTag(DeveroomTagTypes.StepKeyword,
                    GetTextSpan(fileSnapshot, step.Location, step.Keyword),
                    step.Keyword));

            if (step.Argument is DataTable dataTable)
            {
                var dataTableBlockTag = new DeveroomTag(DeveroomTagTypes.DataTable,
                    GetBlockSpan(fileSnapshot, dataTable.Rows.First().Location,
                        dataTable.Rows.Last().Location.Line),
                    dataTable);
                stepTag.AddChild(dataTableBlockTag);
                var dataTableHeader = dataTable.Rows.FirstOrDefault();
                if (dataTableHeader != null)
                    TagRowCells(fileSnapshot, dataTableHeader, dataTableBlockTag, DeveroomTagTypes.DataTableHeader);
            }
            else if (step.Argument is DocString docString)
            {
                stepTag.AddChild(
                    new DeveroomTag(DeveroomTagTypes.DocString,
                        GetBlockSpan(fileSnapshot, docString.Location,
                            GetStepLastLine(step)),
                        docString));
            }

            if (scenarioDefinition is ScenarioOutline) AddPlaceholderTags(fileSnapshot, stepTag, step);

            if (bindingRegistry == ProjectBindingRegistry.Invalid)
                continue;

            var match = bindingRegistry.MatchStep(step, scenarioDefinitionTag);
            AddStepBindingMatchTags(fileSnapshot, stepTag, step, scenarioDefinition, match);
        }

        if (scenarioDefinition is ScenarioOutline scenarioOutline)
            foreach (var scenarioOutlineExample in scenarioOutline.Examples)
            {
                var examplesBlockTag = CreateDefinitionBlockTag(scenarioOutlineExample,
                    DeveroomTagTypes.ExamplesBlock, fileSnapshot,
                    GetExamplesLastLine(scenarioOutlineExample), scenarioDefinitionTag);
                if (scenarioOutlineExample.TableHeader != null)
                    TagRowCells(fileSnapshot, scenarioOutlineExample.TableHeader, examplesBlockTag,
                        DeveroomTagTypes.ScenarioOutlinePlaceholder);
            }

        if (scenarioDefinition is Scenario scenario && bindingRegistry != ProjectBindingRegistry.Invalid)
        {
            var match = bindingRegistry.MatchScenarioToHooks(scenario, scenarioDefinitionTag);
            if (match.HasHooks)
            {
                var firstTagTag = scenarioDefinitionTag
                    .GetDescendantsOfType(DeveroomTagTypes.Tag)
                    .OrderBy(t => t.Range.Start)
                    .FirstOrDefault();

                var startTag = firstTagTag ?? scenarioDefinitionTag;
                var span = new GherkinRange(fileSnapshot, startTag.Range.Start, scenarioDefinitionTag.Range.End - startTag.Range.Start);

                var hookReferenceTag = new DeveroomTag(DeveroomTagTypes.ScenarioHookReference, span, match);
                scenarioDefinitionTag.AddChild(hookReferenceTag);
            }
        }
    }

    /// <summary>
    /// Adds the tag(s) reflecting a step's binding-match outcome (ambiguous/defined/undefined/
    /// error), classified independently of the AST-walking loop that produced <paramref
    /// name="match"/> — the four classifications aren't mutually exclusive at the type level
    /// (e.g. a step can be both undefined-in-one-scope and erroring in another via multi-scope
    /// matching), so each is checked separately rather than via a single switch.
    /// </summary>
    private void AddStepBindingMatchTags(IGherkinTextSnapshot fileSnapshot, DeveroomTag stepTag, Step step,
        StepsContainer scenarioDefinition, MatchResult match)
    {
        if (match.HasAmbiguous)
        {
            // Ambiguous: more than one binding matches — highlighted distinctly so the conflict
            // is visible in the editor. Parameter tags are omitted because there is no single
            // canonical binding whose parameters to highlight.
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.AmbiguousStep,
                GetTextSpan(fileSnapshot, step.Location, step.Text, offset: step.Keyword.Length),
                match));
        }
        else if (match.HasDefined)
        {
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.DefinedStep,
                GetTextSpan(fileSnapshot, step.Location, step.Text, offset: step.Keyword.Length),
                match));
            if (!(scenarioDefinition is ScenarioOutline) || !step.Text.Contains("<"))
            {
                var parameterMatch = match.Items.First(m => m.ParameterMatch != null).ParameterMatch;
                AddParameterTags(fileSnapshot, parameterMatch, stepTag, step);
            }
        }

        if (match.HasUndefined)
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.UndefinedStep,
                GetTextSpan(fileSnapshot, step.Location, step.Text, offset: step.Keyword.Length),
                match));

        // Emit BindingError only for genuine errors (parameter-count mismatch, scope errors,
        // etc.).  Ambiguity is already signalled by AmbiguousStep above; adding BindingError
        // on top would cause the step to re-render as error-coloured instead of ambiguous.
        if (match.HasErrors && !match.HasAmbiguous)
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.BindingError,
                GetTextSpan(fileSnapshot, step.Location, step.Text, offset: step.Keyword.Length),
                match.GetErrorMessage()));
    }

    private void TagRowCells(IGherkinTextSnapshot fileSnapshot, TableRow row, DeveroomTag parentTag, string tagType)
    {
        foreach (var cell in row.Cells)
            parentTag.AddChild(new DeveroomTag(tagType,
                GetSpan(fileSnapshot, cell.Location, cell.Value.Length),
                cell));
    }

    private void AddParameterTags(IGherkinTextSnapshot fileSnapshot, ParameterMatch parameterMatch, DeveroomTag stepTag,
        Step step)
    {
        foreach (var parameter in parameterMatch.StepTextParameters)
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.StepParameter,
                GetSpan(fileSnapshot, step.Location, parameter.Length, step.Keyword.Length + parameter.Index),
                parameter));
    }

    private void AddPlaceholderTags(IGherkinTextSnapshot fileSnapshot, DeveroomTag stepTag, Step step)
    {
        var placeholders = MatchedScenarioOutlinePlaceholder.MatchScenarioOutlinePlaceholders(step);
        foreach (var placeholder in placeholders)
            stepTag.AddChild(new DeveroomTag(DeveroomTagTypes.ScenarioOutlinePlaceholder,
                GetSpan(fileSnapshot, step.Location, placeholder.Length, step.Keyword.Length + placeholder.Index),
                placeholder));
    }

    private DeveroomTag CreateDefinitionBlockTag(IHasDescription astNode, string tagType, IGherkinTextSnapshot fileSnapshot,
        int lastLine)
        => CreateDefinitionBlockTag(astNode, tagType, fileSnapshot, lastLine, VoidDeveroomTag.Instance);

    private DeveroomTag CreateDefinitionBlockTag(IHasDescription astNode, string tagType, IGherkinTextSnapshot fileSnapshot,
        int lastLine, DeveroomTag parentTag)
    {
        var span = GetBlockSpan(fileSnapshot, ((IHasLocation) astNode).Location, lastLine);
        var blockTag = new DeveroomTag(tagType, span, astNode);
        parentTag.AddChild(blockTag);
        blockTag.AddChild(CreateDefinitionLineKeyword(fileSnapshot, astNode));
        if (astNode is IHasTags hasTags)
            foreach (var gherkinTag in hasTags.Tags)
                blockTag.AddChild(
                    new DeveroomTag(DeveroomTagTypes.Tag,
                        GetTextSpan(fileSnapshot, gherkinTag.Location, gherkinTag.Name),
                        gherkinTag));

        if (!string.IsNullOrEmpty(astNode.Description))
        {
            var startLineNumber = ((IHasLocation) astNode).Location.Line + 1;
            while (string.IsNullOrWhiteSpace(fileSnapshot
                       .GetLineFromLineNumber(GetSnapshotLineNumber(startLineNumber, fileSnapshot)).GetText()))
                startLineNumber++;
            blockTag.AddChild(
                new DeveroomTag(DeveroomTagTypes.Description,
                    GetBlockSpan(fileSnapshot, startLineNumber,
                        CountLines(astNode.Description))));
        }

        return blockTag;
    }

    private int CountLines(string text) => NewLineRe.Matches(text).Count + 1;

    private DeveroomTag CreateDefinitionLineKeyword(IGherkinTextSnapshot fileSnapshot, IHasDescription hasDescription) =>
        new(DeveroomTagTypes.DefinitionLineKeyword,
            GetTextSpan(fileSnapshot, ((IHasLocation) hasDescription).Location, hasDescription.Keyword, 1));

    private IEnumerable<DeveroomTag> GetAllTags(DeveroomTag tag)
    {
        yield return tag;
        foreach (var childTag in tag.ChildTags)
        foreach (var allChildTag in GetAllTags(childTag))
            yield return allChildTag;
    }

    private int GetScenarioDefinitionLastLine(StepsContainer stepsContainer)
    {
        if (stepsContainer is ScenarioOutline scenarioOutline)
        {
            var lastExamples = scenarioOutline.Examples.LastOrDefault();
            if (lastExamples != null) return GetExamplesLastLine(lastExamples);
        }

        var lastStep = stepsContainer.Steps.LastOrDefault();
        if (lastStep == null)
            return stepsContainer.Location.Line;
        return GetStepLastLine(lastStep);
    }

    private static int GetExamplesLastLine(Examples examples)
    {
        var lastRow = examples.TableBody?.LastOrDefault() ?? examples.TableHeader;
        if (lastRow != null)
            return lastRow.Location.Line;
        return examples.Location.Line;
    }

    private int GetStepLastLine(Step step)
    {
        if (step.Argument is DocString docStringArg)
        {
            int lineCount = CountLines(docStringArg.Content);
            return docStringArg.Location.Line + lineCount - 1 + 2;
        }

        if (step.Argument is DataTable dataTable) return dataTable.Rows.Last().Location.Line;
        return step.Location.Line;
    }

    private GherkinRange GetBlockSpan(IGherkinTextSnapshot snapshot, Location? startLocation, int locationEndLine)
    {
        var startLine = GetSnapshotLine(startLocation, snapshot);
        var endLine = snapshot.GetLineFromLineNumber(GetSnapshotLineNumber(locationEndLine, snapshot));

        return GherkinRange.FromLines(snapshot, startLine, endLine);
    }

    private GherkinRange GetBlockSpan(IGherkinTextSnapshot snapshot, int startLineNumber, int lineCount)
    {
        var startLine = snapshot.GetLineFromLineNumber(GetSnapshotLineNumber(startLineNumber, snapshot));
        var endLine = snapshot.GetLineFromLineNumber(GetSnapshotLineNumber(startLineNumber + lineCount - 1, snapshot));

        return GherkinRange.FromLines(snapshot, startLine, endLine);
    }

    private GherkinRange GetTextSpan(IGherkinTextSnapshot snapshot, Location? location, string text, int extraLength = 0,
        int offset = 0) =>
        GetSpan(snapshot, location, text.Length + extraLength, offset);

    private GherkinRange GetSpan(IGherkinTextSnapshot snapshot, Location? location, int length, int offset = 0)
    {
        var line = GetSnapshotLine(location, snapshot);
        var startPoint = GetColumnPoint(line, location);
        startPoint = startPoint + offset;
        return GherkinRange.FromPoint(snapshot, startPoint, length);
    }

    private int GetSnapshotLineNumber(Location? location, IGherkinTextSnapshot snapshot) =>
        GetSnapshotLineNumber(location?.Line ?? 0, snapshot);

    private int GetSnapshotLineNumber(int locationLine, IGherkinTextSnapshot snapshot) =>
        locationLine == 0
            ? 0 // global error
            : locationLine - 1 >= snapshot.LineCount
                ? snapshot.LineCount - 1 // unexpected end of file
                : locationLine - 1;

    private int GetSnapshotColumn(Location? location) =>
        location?.Column == 0
            ? 0 // whole line error
            : location?.Column - 1 ?? 0;

    private int GetColumnPoint(IGherkinTextSnapshotLine line, Location? location) =>
        line.Start + (GetSnapshotColumn(location));

    private IGherkinTextSnapshotLine GetSnapshotLine(Location? location, IGherkinTextSnapshot snapshot) =>
        snapshot.GetLineFromLineNumber(GetSnapshotLineNumber(location, snapshot));
}
