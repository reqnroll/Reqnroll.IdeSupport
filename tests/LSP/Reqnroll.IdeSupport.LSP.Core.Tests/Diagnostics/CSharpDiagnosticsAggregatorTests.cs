using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Diagnostics;

public class CSharpDiagnosticsAggregatorTests
{
    private const string FilePath = @"C:\Project\Steps.cs";
    private readonly CSharpDiagnosticsAggregator _sut = new();

    private static ProjectStepDefinitionBinding StepDefIn(
        string path, string method, int line, int column, string? error, ScenarioBlock block = ScenarioBlock.Given,
        SourceLocation? errorLocation = null) =>
        new(block, new Regex("^x$"), null,
            new ProjectBindingImplementation(method, null, new SourceLocation(path, line, column, line, column + 1)),
            error: error, errorLocation: errorLocation);

    private static ProjectHookBinding HookIn(string path, string method, int line, int column, string? error) =>
        new(new ProjectBindingImplementation(method, null, new SourceLocation(path, line, column, line, column + 1)),
            null, HookType.BeforeScenario, null, error);

    [Fact]
    public void Returns_nothing_when_no_binding_has_an_error()
    {
        var registry = new ProjectBindingRegistry(
            [StepDefIn(FilePath, "Steps.Method()", 10, 5, error: null)], [], projectHash: 1);

        _sut.Aggregate(registry, FilePath).Should().BeEmpty();
    }

    [Fact]
    public void Returns_one_diagnostic_for_an_invalid_step_definition()
    {
        var registry = new ProjectBindingRegistry(
            [StepDefIn(FilePath, "Steps.Method()", 10, 5, error: "must be static")], [], projectHash: 1);

        var diagnostics = _sut.Aggregate(registry, FilePath);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().Be("must be static");
        diagnostics[0].Severity.Should().Be(GherkinDiagnosticSeverity.Error);
        diagnostics[0].Location.SourceFileLine.Should().Be(10);
        diagnostics[0].Location.SourceFileColumn.Should().Be(5);
    }

    [Fact]
    public void Dedupes_multiple_attributes_on_the_same_method_into_one_diagnostic()
    {
        // Same method, three attributes ([When] x2 + [Then]) — same SourceLocation, same error
        // (a type-level validation failure applies uniformly). Reproduces the issue #514 spike
        // finding: without dedup this rendered as triplicate diagnostics on one squiggle.
        var registry = new ProjectBindingRegistry(
            [
                StepDefIn(FilePath, "Steps.Method(int)", 10, 5, error: "must be a class", block: ScenarioBlock.When),
                StepDefIn(FilePath, "Steps.Method(int)", 10, 5, error: "must be a class", block: ScenarioBlock.When),
                StepDefIn(FilePath, "Steps.Method(int)", 10, 5, error: "must be a class", block: ScenarioBlock.Then)
            ], [], projectHash: 1);

        var diagnostics = _sut.Aggregate(registry, FilePath);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().Be("must be a class");
    }

    [Fact]
    public void Attribute_specific_errors_on_the_same_method_are_not_merged()
    {
        // Issue #514 follow-up: two attributes on one method ([Given] with a malformed
        // expression, [When] with a valid one) share Implementation.SourceLocation (the method)
        // but each carries its own ErrorLocation (its own attribute) -- these must stay separate
        // squiggles, unlike the pure-structural-error case above which correctly merges.
        var registry = new ProjectBindingRegistry(
            [
                StepDefIn(FilePath, "Steps.Method()", 10, 5, error: "bad expression",
                    errorLocation: new SourceLocation(FilePath, 9, 5, 9, 20)),
                StepDefIn(FilePath, "Steps.Method()", 10, 5, error: null, block: ScenarioBlock.When)
            ], [], projectHash: 1);

        var diagnostics = _sut.Aggregate(registry, FilePath);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().Be("bad expression");
        // Anchored at the attribute (line 9), not the method (line 10).
        diagnostics[0].Location.SourceFileLine.Should().Be(9);
    }

    [Fact]
    public void Joins_distinct_error_messages_at_the_same_location()
    {
        // A method could carry two hook attributes with different hook-specific errors at the
        // same location (e.g. [BeforeTestRun] and [BeforeFeature], each independently invalid).
        var registry = new ProjectBindingRegistry([], [
            HookIn(FilePath, "Hooks.Method()", 10, 5, error: "reason A"),
            HookIn(FilePath, "Hooks.Method()", 10, 5, error: "reason B")
        ], projectHash: 1);

        var diagnostics = _sut.Aggregate(registry, FilePath);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().Be("reason A\nreason B");
    }

    [Fact]
    public void Ignores_bindings_declared_in_other_files()
    {
        var registry = new ProjectBindingRegistry(
            [StepDefIn(@"C:\Project\Other.cs", "Other.Method()", 1, 1, error: "boom")], [], projectHash: 1);

        _sut.Aggregate(registry, FilePath).Should().BeEmpty();
    }

    [Fact]
    public void Combines_step_definitions_and_hooks_from_the_same_file()
    {
        var registry = new ProjectBindingRegistry(
            [StepDefIn(FilePath, "Steps.A()", 10, 5, error: "step error")],
            [HookIn(FilePath, "Steps.B()", 20, 5, error: "hook error")],
            projectHash: 1);

        var diagnostics = _sut.Aggregate(registry, FilePath);

        diagnostics.Should().HaveCount(2);
        diagnostics.Select(d => d.Message).Should().BeEquivalentTo("step error", "hook error");
    }
}
