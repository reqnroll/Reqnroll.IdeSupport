namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// The Roslyn parser and the connector import must give the same <see cref="BindingScope"/> for the
/// same source (issues #953, #1133), so overlaying one discovery result on the other (the registry
/// replaces a file's connector bindings with Roslyn's on every edit) never changes what a binding
/// matches or how many bindings there are. Each case pairs a C# source with the entries Reqnroll's
/// runtime registers for it: one per scope, over the type scopes, the method scopes and, for a
/// hook, each hook tag (<c>BindingSourceProcessor.ProcessMethod</c>/<c>ProcessHookAttribute</c>).
/// </summary>
public class ScopeParityTests : ProjectBindingRegistryTestsBase
{
    private const string FilePath = @"C:\Project\Steps.cs";

    public record Case(string Name, string TypeAttributes, string MethodAttributes, params (string? Tag, string? Feature, string? Scenario)[] Runtime)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<Case> StepCases => new()
    {
        new Case("no scope", "", ""),
        new Case("two tag scopes", "", """[Scope(Tag = "@a")] [Scope(Tag = "@b")]""", ("@a", null, null), ("@b", null, null)),
        new Case("tag or feature", "", """[Scope(Tag = "@web")] [Scope(Feature = "my feature")]""", ("@web", null, null), (null, "my feature", null)),
        new Case("class tag, method feature", """[Scope(Tag = "@ui")]""", """[Scope(Feature = "my feature")]""", ("@ui", null, null), (null, "my feature", null)),
        new Case("class and method tags", """[Scope(Tag = "@ui")]""", """[Scope(Tag = "@web")]""", ("@ui", null, null), ("@web", null, null)),
        new Case("bare scope", "", """[Scope] [Scope(Tag = "@web")]""", (null, null, null), ("@web", null, null)),
        new Case("one scope, two limits", "", """[Scope(Tag = "@web", Feature = "my feature")]""", ("@web", "my feature", null)),
        new Case("tag name without at sign", "", """[Scope(Tag = "web")]""", ("@web", null, null)),
        new Case("duplicate scopes", "", """[Scope(Tag = "@a")] [Scope(Tag = "@a")]""", ("@a", null, null), ("@a", null, null)),
        new Case("same feature, different tags", "", """[Scope(Tag = "@a", Feature = "my feature")] [Scope(Tag = "@b", Feature = "my feature")]""",
            ("@a", "my feature", null), ("@b", "my feature", null)),
        new Case("feature absorbs its tagged scope", "", """[Scope(Feature = "my feature")] [Scope(Tag = "@a", Feature = "my feature")]""",
            (null, "my feature", null), ("@a", "my feature", null)),
    };

    public static TheoryData<Case> HookCases => new()
    {
        new Case("hook tags only", "", "", ("@web", null, null), ("@smoke", null, null)),
        new Case("hook tags and feature", "", """[Scope(Feature = "my feature")]""", ("@web", null, null), ("@smoke", null, null), (null, "my feature", null)),
        new Case("hook tags and class tag", """[Scope(Tag = "@ui")]""", "", ("@ui", null, null), ("@web", null, null), ("@smoke", null, null)),
    };

    private static readonly string[][] ScenarioTagSets =
    {
        Array.Empty<string>(), new[] { "@a" }, new[] { "@b" }, new[] { "@web" }, new[] { "@ui" }, new[] { "@smoke" }, new[] { "@a", "@web" },
    };

    [Theory]
    [MemberData(nameof(StepCases))]
    public async Task Roslyn_and_connector_step_definitions_get_the_same_scope(Case testCase)
    {
        var fromSource = (await Parse(testCase, hook: false)).StepDefinitions.Should().ContainSingle().Subject;
        var fromConnector = ImportSteps(testCase.Runtime);

        fromConnector.Should().ContainSingle("the runtime's entries for one attribute are imported as one binding");
        AssertSameScope(fromSource.Scope, fromConnector[0].Scope, tags => fromSource.Match(CreateStep(), CreateScenarioContext(null, tags)) != null,
            tags => fromConnector[0].Match(CreateStep(), CreateScenarioContext(null, tags)) != null);
    }

    [Theory]
    [MemberData(nameof(HookCases))]
    public async Task Roslyn_and_connector_hooks_get_the_same_scope(Case testCase)
    {
        var fromSource = (await Parse(testCase, hook: true)).Hooks.Should().ContainSingle().Subject;
        var fromConnector = ImportHooks(testCase.Runtime);

        fromConnector.Should().ContainSingle();
        AssertSameScope(fromSource.Scope, fromConnector[0].Scope,
            tags => fromSource.Match(null!, CreateScenarioContext(null, tags)),
            tags => fromConnector[0].Match(null!, CreateScenarioContext(null, tags)));
    }

    [Theory]
    [MemberData(nameof(StepCases))]
    public void The_order_the_runtime_reports_scopes_in_does_not_matter(Case testCase)
    {
        var forward = ImportSteps(testCase.Runtime);
        var backward = ImportSteps(testCase.Runtime.Reverse().ToArray());

        backward.Single().Scope?.ToString().Should().Be(forward.Single().Scope?.ToString());
    }

    [Fact]
    public void Attributes_that_differ_in_anything_but_scope_stay_separate_bindings()
    {
        var steps = new[]
        {
            Step("Given", "one", ("@a", null, null)), Step("Given", "one", ("@b", null, null)),
            Step("Given", "two", ("@a", null, null)), Step("When", "one", ("@a", null, null)),
        };

        var groups = BindingImporter.GroupByAttribute(steps);

        groups.Should().HaveCount(3);
        groups[0].Scopes.Should().HaveCount(2);
    }

    private static void AssertSameScope(BindingScope? fromSource, BindingScope? fromConnector,
        Func<string[], bool> sourceMatches, Func<string[], bool> connectorMatches)
    {
        (fromSource == null).Should().Be(fromConnector == null);
        fromSource?.ToString().Should().Be(fromConnector?.ToString());
        fromSource?.Alternatives?.Count.Should().Be(fromConnector?.Alternatives?.Count);
        foreach (var tags in ScenarioTagSets)
            sourceMatches(tags).Should().Be(connectorMatches(tags), $"scenario tags [{string.Join(", ", tags)}]");
    }

    private static Task<StepDefinitionFileBindings> Parse(Case testCase, bool hook)
    {
        var attribute = hook ? """[BeforeScenario("web", "smoke")]""" : """[Given("my step")]""";
        var content = $$"""
            using Reqnroll;
            namespace TestProject
            {
                [Binding]
                {{testCase.TypeAttributes}}
                public class Steps
                {
                    {{testCase.MethodAttributes}}
                    {{attribute}}
                    public void Method() { }
                }
            }
            """;
        return new StepDefinitionFileParser().ParseBindings(FileDetails.FromPath(FilePath).WithCSharpContent(content));
    }

    private static BindingImporter CreateImporter() =>
        new(new Dictionary<string, string>(), new Dictionary<string, string>(), new IdeSupportNullLogger(), new FileSystemForIDE());

    private static StepScope? ToScope((string? Tag, string? Feature, string? Scenario) scope) =>
        scope == (null, null, null) ? new StepScope() : new StepScope { Tag = scope.Tag, FeatureTitle = scope.Feature, ScenarioTitle = scope.Scenario };

    private static StepDefinition Step(string type, string regex, params (string? Tag, string? Feature, string? Scenario)[] runtime) =>
        Step(type, regex, runtime.Length == 0 ? null : ToScope(runtime[0]));

    private static StepDefinition Step(string type, string regex, StepScope? scope) =>
        new() { Type = type, Regex = regex, Method = "Steps.Method()", Scope = scope };

    private static IReadOnlyList<ProjectStepDefinitionBinding> ImportSteps((string? Tag, string? Feature, string? Scenario)[] runtime)
    {
        var importer = CreateImporter();
        var dtos = runtime.Length == 0
            ? new[] { Step("Given", "my step", (StepScope?)null) }
            : runtime.Select(scope => Step("Given", "my step", ToScope(scope))).ToArray();
        return BindingImporter.GroupByAttribute(dtos)
            .Select(group => importer.ImportStepDefinition(group.Binding, scopes: group.Scopes)).ToArray();
    }

    private static IReadOnlyList<ProjectHookBinding> ImportHooks((string? Tag, string? Feature, string? Scenario)[] runtime)
    {
        var importer = CreateImporter();
        var dtos = runtime.Select(scope => new Hook { Type = "BeforeScenario", Method = "Steps.Method()", Scope = ToScope(scope) }).ToArray();
        return BindingImporter.GroupByAttribute(dtos)
            .Select(group => importer.ImportHook(group.Binding, scopes: group.Scopes)).ToArray();
    }
}
