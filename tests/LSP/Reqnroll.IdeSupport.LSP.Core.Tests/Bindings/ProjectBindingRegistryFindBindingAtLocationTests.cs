namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

public class ProjectBindingRegistryFindBindingAtLocationTests
{
    private static ProjectStepDefinitionBinding CreateBinding(SourceLocation location, string methodName = "MyStep",
        int? attributeSourceLine = null) =>
        new(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation(methodName, null, location),
            attributeSourceLine: attributeSourceLine);

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    [Fact]
    public void Exact_line_and_column_match_resolves()
    {
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 10, 5))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void Same_line_different_column_still_resolves()
    {
        // Column is intentionally ignored — a click anywhere on the method identifier's line
        // should resolve, not just the exact recorded column.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 10, 30))
            .Should().BeSameAs(binding);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void Click_on_the_attribute_line_above_the_method_identifier_resolves(int attributeLine)
    {
        // The [Given] attribute is typically 1-2 lines above the discovered method identifier
        // (Roslyn-path) or method-body start (connector-path) — F2 rename is invoked from the
        // attribute, so it must resolve, not just the exact recorded line.
        // Without AttributeSourceLine, this uses the 2-line heuristic fallback.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", attributeLine, 5))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void More_than_two_lines_before_the_recorded_line_does_not_resolve()
    {
        // Without AttributeSourceLine, the 2-line heuristic caps at 2 lines above.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 7, 5))
            .Should().BeNull();
    }

    [Fact]
    public void Line_within_the_recorded_end_line_resolves()
    {
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5, sourceFileEndLine: 15));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 13, 1))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void Line_past_the_recorded_end_line_does_not_resolve()
    {
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5, sourceFileEndLine: 15));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 16, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Different_file_does_not_resolve()
    {
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("OtherSteps.cs", 10, 5))
            .Should().BeNull();
    }

    [Fact]
    public void File_comparison_is_case_insensitive()
    {
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("STEPS.CS", 10, 5))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void No_binding_with_a_source_location_returns_null()
    {
        var binding = new ProjectStepDefinitionBinding(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation("MyStep", null, null!));
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 10, 5))
            .Should().BeNull();
    }

    // ── AST-based (AttributeSourceLine) tests ──────────────────────────────

    [Fact]
    public void Binding_with_attribute_source_line_matches_that_exact_line()
    {
        // Attribute on line 8, method on line 10 — query on the attribute line resolves.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5),
            attributeSourceLine: 8);
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 8, 1))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void Binding_with_attribute_source_line_also_matches_method_line()
    {
        // Query on the method identifier line should also resolve.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5),
            attributeSourceLine: 8);
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 10, 1))
            .Should().BeSameAs(binding);
    }

    [Fact]
    public void Binding_with_attribute_source_line_does_not_match_adjacent_non_attribute_line()
    {
        // Line 9 is between the [Given] attribute (line 8) and the method (line 10) —
        // e.g. a blank line or comment. With exact attribute-line matching, this should NOT resolve.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5),
            attributeSourceLine: 8);
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 9, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Binding_with_attribute_source_line_rejects_line_too_far_above()
    {
        // Line 6 is 4 lines above the method — well outside any reasonable window.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5),
            attributeSourceLine: 8);
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 6, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Multiple_bindings_with_different_attribute_lines_resolve_correctly()
    {
        // Method A at line 10, attribute on line 8; Method B at line 25, attribute on line 23.
        var bindingA = CreateBinding(new SourceLocation("Steps.cs", 10, 5), methodName: "GivenA",
            attributeSourceLine: 8);
        var bindingB = CreateBinding(new SourceLocation("Steps.cs", 25, 5), methodName: "GivenB",
            attributeSourceLine: 23);
        var registry = RegistryWith(bindingA, bindingB);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 8, 1))
            .Should().BeSameAs(bindingA);
        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 25, 1))
            .Should().BeSameAs(bindingB);
        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 23, 1))
            .Should().BeSameAs(bindingB);
        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 9, 1))
            .Should().BeNull();
    }

    [Fact]
    public void Binding_with_attribute_and_method_on_the_same_line_matches()
    {
        // Single-line style: [Given("x")] public void MyStep() { } — attribute and method
        // identifier share the same source line.
        var binding = CreateBinding(new SourceLocation("Steps.cs", 10, 5),
            attributeSourceLine: 10);
        var registry = RegistryWith(binding);

        registry.FindBindingAtLocation(new SourceLocation("Steps.cs", 10, 1))
            .Should().BeSameAs(binding);
    }
}