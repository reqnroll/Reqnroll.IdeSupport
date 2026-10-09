namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Issue #953: several <c>[Scope]</c> attributes on the same method (or type) are alternatives
/// (OR) at runtime -- Reqnroll's <c>BindingSourceProcessor.ApplyForScope</c> registers one binding
/// per scope -- while the Tag/Feature/Scenario properties inside a single <c>[Scope(...)]</c> are
/// AND-ed (<c>BindingScope.Match</c>). These tests parse real C# source with
/// <see cref="StepDefinitionFileParser"/> and match the resulting bindings against a Gherkin
/// context whose feature is "my feature" and whose scenario is "my scenario".
/// </summary>
public class StepDefinitionFileParserMultipleScopeTests : ProjectBindingRegistryTestsBase
{
    private const string FilePath = @"C:\Project\Steps.cs";

    private static Task<StepDefinitionFileBindings> ParseBindings(string body, string typeAttributes = "")
    {
        var content = $@"
using Reqnroll;
namespace TestProject
{{
    [Binding]
    {typeAttributes}
    public class Steps
    {{
{body}
    }}
}}";
        var file = FileDetails.FromPath(FilePath).WithCSharpContent(content);
        return new StepDefinitionFileParser().ParseBindings(file);
    }

    private async Task<ProjectStepDefinitionBinding> ParseSingleStepDefinition(string scopeAttributes,
        string typeAttributes = "")
    {
        var bindings = await ParseBindings($@"{scopeAttributes}
              [Given(""my step"")]
              public void Method() {{ }}", typeAttributes);
        return bindings.StepDefinitions.Should().ContainSingle().Subject!;
    }

    private bool Matches(ProjectStepDefinitionBinding binding, params string[] scenarioTags) =>
        binding.Match(CreateStep(), CreateScenarioContext(null, scenarioTags)) != null;

    [Fact]
    public async Task Tag_scope_or_feature_scope_matches_an_untagged_scenario_of_that_feature()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@web"")]
              [Scope(Feature = ""my feature"")]");

        binding.IsValid.Should().BeTrue();
        Matches(binding).Should().BeTrue("the [Scope(Feature)] alternative matches on its own");
    }

    [Fact]
    public async Task Tag_scope_or_feature_scope_matches_a_tagged_scenario_of_another_feature()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@web"")]
              [Scope(Feature = ""other feature"")]");

        Matches(binding, "@web").Should().BeTrue("the [Scope(Tag)] alternative matches on its own");
        Matches(binding).Should().BeFalse("neither alternative matches an untagged scenario of 'my feature'");
    }

    [Fact]
    public async Task Any_of_several_feature_scopes_matches()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Feature = ""other feature"")]
              [Scope(Feature = ""my feature"")]");

        Matches(binding).Should().BeTrue("the second [Scope(Feature)] names this feature");
    }

    [Fact]
    public async Task Any_of_several_scenario_scopes_matches()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Scenario = ""other scenario"")]
              [Scope(Scenario = ""my scenario"")]");

        Matches(binding).Should().BeTrue("the second [Scope(Scenario)] names this scenario");
    }

    [Fact]
    public async Task An_unrestricted_scope_alternative_matches_every_scenario()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope]
              [Scope(Tag = ""@web"")]");

        Matches(binding).Should().BeTrue("a bare [Scope] alternative restricts nothing");
    }

    [Fact]
    public async Task Properties_of_a_single_scope_attribute_stay_and_combined()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@web"", Feature = ""other feature"")]");

        Matches(binding, "@web").Should().BeFalse("Tag and Feature inside one [Scope] must both match");
    }

    [Fact]
    public async Task Tag_only_scopes_keep_the_same_combined_tag_expression()
    {
        // Byte-for-byte compatibility for the already-correct tag-only case: two tag scopes are
        // still represented as one OR-ed tag expression, identical to writing it by hand.
        var multiple = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@a"")]
              [Scope(Tag = ""@b"")]");
        var single = await ParseSingleStepDefinition(@"[Scope(Tag = ""(@a) or (@b)"")]");

        multiple.Scope!.ToString().Should().Be(single.Scope!.ToString());
        multiple.Scope.Tag!.ToString().Should().Be(single.Scope.Tag!.ToString());
        multiple.Scope.Alternatives.Should().BeNull();
    }

    [Fact]
    public async Task Mixed_scopes_are_kept_as_alternatives_and_formatted_as_such()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@web"")]
              [Scope(Feature = ""my feature"")]");

        binding.Scope!.Tag.Should().BeNull();
        binding.Scope.FeatureTitle.Should().BeNull();
        binding.Scope.Alternatives.Should().HaveCount(2);
        binding.Scope.ToString().Should().Be("(@web) or (Feature='my feature')");
    }

    [Fact]
    public async Task Type_level_scope_is_and_combined_with_each_method_scope_alternative()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@web"")]
              [Scope(Feature = ""my feature"")]",
            typeAttributes: @"[Scope(Tag = ""@ui"")]");

        Matches(binding, "@ui").Should().BeTrue("@ui (type) and Feature='my feature' (method alternative) both hold");
        Matches(binding).Should().BeFalse("the type-level @ui scope still applies to every alternative");
    }

    [Fact]
    public async Task Hook_with_feature_or_tag_scopes_matches_either_alternative()
    {
        var bindings = await ParseBindings(
            @"[Scope(Feature = ""other feature"")]
              [Scope(Tag = ""@web"")]
              [BeforeScenario]
              public void Setup() { }");

        var hook = bindings.Hooks.Should().ContainSingle().Subject!;
        hook.Match(null!, CreateScenarioContext(null, "@web")).Should().BeTrue();
        hook.Match(null!, CreateScenarioContext(null)).Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_tag_expression_in_one_alternative_invalidates_the_binding()
    {
        var binding = await ParseSingleStepDefinition(
            @"[Scope(Tag = ""@a and"")]
              [Scope(Feature = ""my feature"")]");

        binding.IsValid.Should().BeFalse();
        binding.Error.Should().Contain("Invalid tag expression '@a and'");
    }
}
