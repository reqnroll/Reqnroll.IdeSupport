using Gherkin;
using GherkinLocation = Gherkin.Ast.Location;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

public abstract class ProjectBindingRegistryTestsBase
{
    protected readonly List<ProjectStepDefinitionBinding> _stepDefinitionBindings = new();
    protected readonly List<ProjectHookBinding> _hookBindings = new();
    protected readonly Dictionary<string, ProjectBindingImplementation> Implementations = new();

    protected ProjectBindingRegistry CreateSut()
    {
        var projectBindingRegistry = new ProjectBindingRegistry(_stepDefinitionBindings.ToArray(), _hookBindings.ToArray(), 123456);
        return projectBindingRegistry;
    }

    protected Step CreateStep(StepKeyword stepKeyword = StepKeyword.Given, string text = "my step",
        StepArgument? stepArgument = null) => new DeveroomGherkinStep(new GherkinLocation(0, 0), stepKeyword + " ", StepKeywordType.Context, text, stepArgument!,
        stepKeyword, (ScenarioBlock) stepKeyword);

    protected ProjectStepDefinitionBinding CreateStepDefinitionBinding(string regex,
        ScenarioBlock scenarioBlock = ScenarioBlock.Given, BindingScope? scope = null, string[]? parameterTypes = null,
        string? methodName = null)
    {
        methodName = methodName ?? "MyMethod" + Guid.NewGuid().ToString("N");
        if (!Implementations.TryGetValue(methodName, out var implementation))
        {
            implementation =
                new ProjectBindingImplementation(methodName, parameterTypes,
                    new SourceLocation("MyClass.cs", 2, 5));
            Implementations.Add(methodName, implementation);
        }

        // specifiedExpression mirrors regex: these bindings represent an authored expression
        // (e.g. [Given(@"...")]), not a method-name-style binding (issue #344 gave
        // SpecifiedExpression == null a real meaning — see CreateMethodNameStyleStepDefinitionBinding).
        return new ProjectStepDefinitionBinding(scenarioBlock, new Regex("^" + regex + "$"), scope, implementation,
            specifiedExpression: regex);
    }

    /// <summary>
    /// Creates a method-name-style binding — no explicit attribute expression, mirroring
    /// <c>[Given] public void The_First_Number_Is_P0(int p)</c> — for tests asserting on issue
    /// #344's DisplayExpression fallback in ambiguous-match diagnostics.
    /// </summary>
    protected ProjectStepDefinitionBinding CreateMethodNameStyleStepDefinitionBinding(string regex,
        ScenarioBlock scenarioBlock = ScenarioBlock.Given, string? methodName = null)
    {
        methodName = methodName ?? "MyMethod" + Guid.NewGuid().ToString("N");
        if (!Implementations.TryGetValue(methodName, out var implementation))
        {
            implementation =
                new ProjectBindingImplementation(methodName, null,
                    new SourceLocation("MyClass.cs", 2, 5));
            Implementations.Add(methodName, implementation);
        }

        return new ProjectStepDefinitionBinding(scenarioBlock, new Regex("^" + regex + "$"), null, implementation);
    }

    protected StepArgument CreateDocString() => new DocString(new GherkinLocation(0, 0), null, "some text");

    protected static DataTable CreateDataTable()
    {
        return new DataTable(new List<TableRow>
        {
            new TableRow(new GherkinLocation(0, 0), new[] {new TableCell(new GherkinLocation(0, 0), "cell1")})
        });
    }

    protected BindingScope CreateTagScope(string tagName) => new() {Tag = ReqnrollTagExpressionParser.CreateTagLiteral(tagName)};

    private DeveroomTag CreateFeatureStructure(string[]? featureTags, string[]? scenarioTags,
        string[]? scenarioOutlineTags = null, string[]? soHeaders = null, string[][]? soCells = null,
        bool includeScenario = true, bool includeOutline = true, string[]? outlineExamplesTags = null)
    {
        featureTags = featureTags ?? new string[0];
        scenarioTags = scenarioTags ?? new string[0];
        scenarioOutlineTags = scenarioOutlineTags ?? new string[0];
        outlineExamplesTags = outlineExamplesTags ?? new string[0];
        soHeaders = soHeaders ?? new[] {"param1", "param2"};
        soCells = soCells ?? new[] {new[] {"r1c1", "r1c2"}, new[] {"r2c1", "r2c2"}};

        var scenarioDefinitions = new List<StepsContainer>();
        scenarioDefinitions.Add(new Background(new GherkinLocation(0, 0), "Background", "my background", null, new Step[0]));
        if (includeScenario)
            scenarioDefinitions.Add(new SingleScenario(scenarioTags.Select(t => new Tag(new GherkinLocation(0, 0), t)).ToArray(), new GherkinLocation(0, 0),
                "Scenario", "my scenario", null, new Step[0]));
        if (includeOutline)
            scenarioDefinitions.Add(new ScenarioOutline(scenarioOutlineTags.Select(t => new Tag(new GherkinLocation(0, 0), t)).ToArray(),
                new GherkinLocation(0, 0), "Scenario Outline", "my scenario outline", null!, new Step[0], new[]
                {
                    new Examples(outlineExamplesTags.Select(t => new Tag(new GherkinLocation(0, 0), t)).ToArray(), new GherkinLocation(0, 0), "Examples",
                        "my examples",
                        null, new TableRow(new GherkinLocation(0, 0), soHeaders.Select(h => new TableCell(new GherkinLocation(0, 0), h)).ToArray()),
                        soCells.Select(r => new TableRow(new GherkinLocation(0, 0), r.Select(c => new TableCell(new GherkinLocation(0, 0), c)).ToArray()))
                            .ToArray())
                }));

        var feature = new Feature(featureTags.Select(t => new Tag(new GherkinLocation(0, 0), t)).ToArray(), new GherkinLocation(0, 0), "en", "Feature",
            "my feature", null, scenarioDefinitions.ToArray());
        var featureTag = new DeveroomTag(DeveroomTagTypes.FeatureBlock, default, feature);
        var backgroundTag = new DeveroomTag(DeveroomTagTypes.ScenarioDefinitionBlock, default,
            feature.Children.OfType<Background>().First());
        featureTag.AddChild(backgroundTag);
        if (includeScenario)
        {
            var scenarioTag = new DeveroomTag(DeveroomTagTypes.ScenarioDefinitionBlock, default,
                feature.Children.OfType<Scenario>().First());
            featureTag.AddChild(scenarioTag);
        }

        if (includeOutline)
        {
            var scenarioOutlineTag = new DeveroomTag(DeveroomTagTypes.ScenarioDefinitionBlock, default,
                feature.Children.OfType<ScenarioOutline>().First());
            featureTag.AddChild(scenarioOutlineTag);
        }

        return featureTag;
    }

    protected IGherkinDocumentContext CreateScenarioContext(string[]? featureTags, params string[] scenarioTags)
    {
        var featureTag = CreateFeatureStructure(featureTags, scenarioTags);
        return featureTag.ChildTags.First(t => t.Data is Scenario);
    }

    protected IGherkinDocumentContext CreateScenarioOutlineContext(string[]? featureTags, string[]? scenarioOutlineTags,
        string soHeader, string[] soCells, string[]? outlineExamplesTags = null)
    {
        var featureTag = CreateFeatureStructure(featureTags, null, scenarioOutlineTags, new[] {soHeader},
            soCells.Select(r => new[] {r}).ToArray(), outlineExamplesTags: outlineExamplesTags);
        return featureTag.ChildTags.First(t => t.Data is ScenarioOutline);
    }

    protected IGherkinDocumentContext CreateScenarioOutlineContext(string[]? featureTags = null,
        string[]? scenarioOutlineTags = null, string[]? soHeaders = null, string[][]? soCells = null)
    {
        var featureTag = CreateFeatureStructure(featureTags, null, scenarioOutlineTags, soHeaders, soCells);
        return featureTag.ChildTags.First(t => t.Data is ScenarioOutline);
    }

    protected IGherkinDocumentContext CreateBackgroundContext(string[]? featureTags = null, string[]? scenarioTags = null,
        string[]? scenarioOutlineTags = null, string[]? outlineExamplesTags = null)
    {
        var featureTag = CreateFeatureStructure(featureTags, scenarioTags, scenarioOutlineTags,
            outlineExamplesTags: outlineExamplesTags);
        return featureTag.ChildTags.First(t => t.Data is Background);
    }

    protected IGherkinDocumentContext CreateEmptyFileBackgroundContext(string[]? featureTags)
    {
        var featureTag = CreateFeatureStructure(featureTags, null, includeScenario: false, includeOutline: false);
        return featureTag.ChildTags.First(t => t.Data is Background);
    }

    protected string[]? GetParameterTypes(params string[]? typeNames)
    {
        if (typeNames == null || typeNames.Length == 0)
            return null;

        return typeNames.Select(GetParameterType).ToArray();
    }

    protected string GetParameterType(string typeName)
    {
        switch (typeName)
        {
            case "string":
                return typeof(string).FullName!;
            case "int":
                return typeof(int).FullName!;
            case "DataTable":
                return "Reqnroll.Table";
            default:
                return typeName;
        }
    }
}
