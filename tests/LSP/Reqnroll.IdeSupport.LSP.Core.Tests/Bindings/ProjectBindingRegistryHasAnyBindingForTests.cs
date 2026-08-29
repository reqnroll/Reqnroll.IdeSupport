namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// Unit tests for <see cref="ProjectBindingRegistry.HasAnyBindingFor"/> (issue #517): the direct
/// per-file question used to replace the project-level <c>HasSuccessfulConnectorRun</c> proxy in
/// <c>CSharpBindingDiscoveryService</c>'s didOpen skip-check.
/// </summary>
public class ProjectBindingRegistryHasAnyBindingForTests
{
    private static ProjectStepDefinitionBinding CreateStepBinding(string sourceFile, string methodName = "MyStep") =>
        new(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation(methodName, null, new SourceLocation(sourceFile, 10, 5)));

    private static ProjectHookBinding CreateHookBinding(string sourceFile) =>
        new(new ProjectBindingImplementation("HookMethod", null, new SourceLocation(sourceFile, 10, 1)),
            null, HookType.BeforeScenario, null, null);

    [Fact]
    public void Returns_true_when_a_step_definition_binding_matches_the_file()
    {
        var registry = new ProjectBindingRegistry(
            new[] { CreateStepBinding("Steps.cs") }, Array.Empty<ProjectHookBinding>(), 0);

        registry.HasAnyBindingFor("Steps.cs").Should().BeTrue();
    }

    [Fact]
    public void Returns_true_when_a_hook_binding_matches_the_file()
    {
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), new[] { CreateHookBinding("Hooks.cs") }, 0);

        registry.HasAnyBindingFor("Hooks.cs").Should().BeTrue();
    }

    [Fact]
    public void Returns_false_when_no_binding_matches_the_file()
    {
        var registry = new ProjectBindingRegistry(
            new[] { CreateStepBinding("Steps.cs") }, new[] { CreateHookBinding("Hooks.cs") }, 0);

        registry.HasAnyBindingFor("OtherFile.cs").Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_an_empty_registry()
    {
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), 0);

        registry.HasAnyBindingFor("Steps.cs").Should().BeFalse();
    }

    [Fact]
    public void File_comparison_is_case_insensitive()
    {
        var registry = new ProjectBindingRegistry(
            new[] { CreateStepBinding("Steps.cs") }, Array.Empty<ProjectHookBinding>(), 0);

        registry.HasAnyBindingFor("STEPS.CS").Should().BeTrue();
    }

    [Fact]
    public void Returns_false_when_binding_has_no_source_location()
    {
        var binding = new ProjectStepDefinitionBinding(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation("MyStep", null, null!));
        var registry = new ProjectBindingRegistry(
            new[] { binding }, Array.Empty<ProjectHookBinding>(), 0);

        registry.HasAnyBindingFor("Steps.cs").Should().BeFalse();
    }
}
