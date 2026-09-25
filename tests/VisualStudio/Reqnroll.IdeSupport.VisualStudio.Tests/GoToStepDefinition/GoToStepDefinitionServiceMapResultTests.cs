using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToStepDefinition;

/// <summary>
/// Client-side mapping of a <c>reqnroll/goToStepDefinition</c> result into step-definition rows
/// (<see cref="GoToStepDefinitionService.MapResult"/>, issue #757).
/// </summary>
public class GoToStepDefinitionServiceMapResultTests
{
    private static JObject Item(string className, string methodName, string? expression, string? sourceFile, int line, int character,
        bool isResolved = true, string? recordedSourceFile = null) => new()
    {
        ["className"]          = className,
        ["methodName"]         = methodName,
        ["bindingExpression"]  = expression,
        ["sourceFile"]         = sourceFile,
        ["sourceLine"]         = line,
        ["sourceChar"]         = character,
        ["isResolved"]         = isResolved,
        ["recordedSourceFile"] = recordedSourceFile,
    };

    private static JObject Response(params JObject[] items) => new() { ["items"] = new JArray(items) };

    [Fact]
    public void A_null_or_non_object_result_has_no_rows()
    {
        GoToStepDefinitionService.MapResult(null).Should().BeEmpty();
        GoToStepDefinitionService.MapResult(JValue.CreateNull()).Should().BeEmpty();
        GoToStepDefinitionService.MapResult(new JArray()).Should().BeEmpty();
        GoToStepDefinitionService.MapResult(new JObject()).Should().BeEmpty();
    }

    [Fact]
    public void Each_item_maps_class_method_expression_and_position_in_order()
    {
        var rows = GoToStepDefinitionService.MapResult(Response(
            Item("CalculatorSteps", "GivenTheFirstNumberIs", "the first number is {int}", @"C:\w\Steps.cs", 16, 20),
            Item("CalculatorSteps", "GivenTheFirstNumberIsDuplicate", "the first number is (.*)", @"C:\w\Steps.cs", 24, 20)));

        rows.Select(r => (r.ClassName, r.MethodName, r.BindingExpression, r.SourceFile, r.SourceLine, r.SourceChar))
            .Should().Equal(
                ("CalculatorSteps", "GivenTheFirstNumberIs", "the first number is {int}", @"C:\w\Steps.cs", 16, 20),
                ("CalculatorSteps", "GivenTheFirstNumberIsDuplicate", "the first number is (.*)", @"C:\w\Steps.cs", 24, 20));
    }

    [Fact]
    public void Repeats_of_the_same_method_are_collapsed()
    {
        // One method with two matching [Given] attributes: two bindings, one navigation target.
        var rows = GoToStepDefinitionService.MapResult(Response(
            Item("Steps", "AStep", "a step", @"C:\w\Steps.cs", 16, 20),
            Item("Steps", "AStep", "a {word}", @"C:\w\Steps.cs", 16, 20),
            Item("Steps", "Other", "a step", @"C:\w\Steps.cs", 24, 20)));

        rows.Select(r => r.MethodName).Should().Equal("AStep", "Other");
    }

    [Fact]
    public void An_unresolved_item_keeps_its_recorded_source_and_is_not_navigable()
    {
        var row = GoToStepDefinitionService.MapResult(Response(
                Item("Steps", "AStep", "a step", sourceFile: null, 9, 4, isResolved: false, recordedSourceFile: "/workspaces/host/Steps.cs")))
            .Should().ContainSingle().Subject;

        row.IsResolved.Should().BeFalse();
        row.SourceFile.Should().BeNull();
        row.RecordedSourceFile.Should().Be("/workspaces/host/Steps.cs");
    }
}
