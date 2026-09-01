namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

public class BindingIdTests
{
    // ── Cross-discovery-path identity (issue #547/#548) ────────────────────────
    //
    // The reflection connector and Roslyn source parsing spell the very same method
    // differently: the connector emits "DeclaringType.MethodName(ParamType, ...)" (built from
    // MethodInfo reflection data, no namespace), while Roslyn emits
    // "Namespace.DeclaringType.MethodName" (walking up syntax-tree ancestors), with no parameter
    // list. A binding whose source file is discovered by both paths -- e.g. a class library
    // referenced by another project: the referencing project's connector run transitively picks
    // up the library's bindings, while the library's own registry gets that same file
    // Roslyn-reconciled (an open buffer, or edited-since-build) -- must still hash to the same
    // BindingId, or cross-registry dedup (Find Unused Step Definitions) and usage lookups
    // (CodeLens) silently treat it as two unrelated bindings.

    [Fact]
    public void Connector_and_Roslyn_method_formats_for_the_same_method_produce_the_same_id()
    {
        var connectorId = BindingId.Compute(
            ScenarioBlock.Given, "ExtraSteps.AnUnusedStep()", Array.Empty<string>(), "An Unused Step");
        var roslynId = BindingId.Compute(
            ScenarioBlock.Given, "MyLibrary.StepDefinitions.ExtraSteps.AnUnusedStep", Array.Empty<string>(), "An Unused Step");

        roslynId.Should().Be(connectorId);
    }

    [Fact]
    public void Connector_and_Roslyn_parameter_type_spellings_for_the_same_method_produce_the_same_id()
    {
        // Connector: fully-qualified CLR type name (Type.FullName). Roslyn: literal source text.
        var connectorId = BindingId.Compute(
            ScenarioBlock.Given, "Steps.SetFirstNumber(Int32)", new[] { "System.Int32" }, "x");
        var roslynId = BindingId.Compute(
            ScenarioBlock.Given, "MyApp.Steps.SetFirstNumber", new[] { "int" }, "x");

        roslynId.Should().Be(connectorId);
    }

    [Fact]
    public void Generic_parameter_type_spellings_across_discovery_paths_produce_the_same_id()
    {
        var connectorId = BindingId.Compute(
            ScenarioBlock.Given, "Steps.SetItems(List)",
            new[] { "System.Collections.Generic.List<System.String>" }, "x");
        var roslynId = BindingId.Compute(
            ScenarioBlock.Given, "MyApp.Steps.SetItems",
            new[] { "List<string>" }, "x");

        roslynId.Should().Be(connectorId);
    }

    [Fact]
    public void Different_methods_still_produce_different_ids_after_normalization()
    {
        var id1 = BindingId.Compute(ScenarioBlock.Given, "ExtraSteps.AnUnusedStep()", Array.Empty<string>(), "An Unused Step");
        var id2 = BindingId.Compute(ScenarioBlock.Given, "ExtraSteps.ExternalStep()", Array.Empty<string>(), "A step from an external assembly");

        id1.Should().NotBe(id2);
    }
}
