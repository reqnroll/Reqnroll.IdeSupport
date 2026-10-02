#nullable enable

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class ProjectCharacteristicsTelemetryTests
{
    private static ProjectBindingImplementation Impl(string method) =>
        new(method, null, new SourceLocation("A.cs", 1, 1));

    private static ProjectStepDefinitionBinding Step(string method) =>
        new(ScenarioBlock.Given, new Regex("^x$"), null, Impl(method));

    private static ProjectHookBinding Hook(string method, HookType type) =>
        new(Impl(method), null, type, null, null);

    private static ProjectBindingRegistry Registry(
        IEnumerable<ProjectStepDefinitionBinding> steps, IEnumerable<ProjectHookBinding> hooks) =>
        new(steps.ToImmutableArray(), hooks.ToImmutableArray(), 1);

    [Fact]
    public void Build_counts_steps_hooks_binding_classes_and_hooks_per_type()
    {
        var registry = Registry(
            [Step("My.Ns.Steps.GivenA"), Step("My.Ns.Steps.GivenB"), Step("My.Ns.Other.WhenC")],
            [Hook("My.Ns.Steps.Setup", HookType.BeforeScenario), Hook("My.Ns.Hooks.Setup2", HookType.BeforeScenario),
             Hook("My.Ns.Hooks.Done", HookType.AfterScenario)]);

        var properties = ProjectCharacteristicsTelemetry.Build(registry, 7, "net8.0");

        properties["StepDefinitionCount"].Should().Be(3);
        properties["HookCount"].Should().Be(3);
        // Steps, Other and Hooks: three distinct declaring classes across steps and hooks.
        properties["StepBindingClassCount"].Should().Be(3);
        properties["HookCount_BeforeScenario"].Should().Be(2);
        properties["HookCount_AfterScenario"].Should().Be(1);
        properties.Should().NotContainKey("HookCount_BeforeFeature");
        properties["FeatureFileCount"].Should().Be(7);
        properties["ProjectTargetFramework"].Should().Be("net8.0");
    }

    [Fact]
    public void Build_sends_only_counts_and_the_target_framework_never_names_or_paths()
    {
        var registry = Registry([Step("Secret.Company.Steps.Pay")], [Hook("Secret.Company.Steps.Init", HookType.BeforeFeature)]);

        var properties = ProjectCharacteristicsTelemetry.Build(registry, 1, "net8.0");

        properties.Values.Should().OnlyContain(v => v is int || (v is string && (string)v == "net8.0"));
        properties.Keys.Should().OnlyContain(k => k == "StepDefinitionCount" || k == "HookCount" || k == "StepBindingClassCount"
            || k == "FeatureFileCount" || k == "ProjectTargetFramework" || k.StartsWith("HookCount_"));
    }

    [Fact]
    public void Build_omits_unknown_feature_file_count_and_target_framework_rather_than_sending_zero_or_blank()
    {
        var properties = ProjectCharacteristicsTelemetry.Build(Registry([], []), null, "");

        properties.Should().NotContainKey("FeatureFileCount");
        properties.Should().NotContainKey("ProjectTargetFramework");
        properties["StepDefinitionCount"].Should().Be(0);
        properties["StepBindingClassCount"].Should().Be(0);
    }

    [Fact]
    public void CountBindingClasses_ignores_identities_without_a_class_part()
    {
        var registry = Registry([Step("NoDots"), Step("A.B.M1"), Step("A.B.M2")], []);

        ProjectCharacteristicsTelemetry.CountBindingClasses(registry).Should().Be(1);
    }
}
