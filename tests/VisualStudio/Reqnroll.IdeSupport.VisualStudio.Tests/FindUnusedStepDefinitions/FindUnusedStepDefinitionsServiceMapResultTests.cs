using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.FindUnusedStepDefinitions;

/// <summary>
/// Client-side mapping of a <c>reqnroll/findUnusedStepDefinitions</c> result into an
/// <see cref="UnusedStepDefinitionsResult"/> (<see cref="FindUnusedStepDefinitionsService.MapResult"/>).
/// </summary>
public class FindUnusedStepDefinitionsServiceMapResultTests
{
    private static JObject Item(string expression, string methodName) => new()
    {
        ["projectName"]       = "Sample",
        ["className"]         = "Steps",
        ["methodName"]        = methodName,
        ["bindingExpression"] = expression,
        ["sourceFile"]        = @"c:\w\Steps.cs",
        ["sourceLine"]        = 7,
        ["sourceChar"]        = 4,
    };

    [Fact]
    public void A_null_or_non_object_result_is_empty()
    {
        FindUnusedStepDefinitionsService.MapResult(null).Items.Should().BeEmpty();
        FindUnusedStepDefinitionsService.MapResult(JValue.CreateNull()).Items.Should().BeEmpty();
        FindUnusedStepDefinitionsService.MapResult(new JArray()).Items.Should().BeEmpty();
    }

    [Fact]
    public void A_result_without_items_is_empty()
    {
        FindUnusedStepDefinitionsService.MapResult(new JObject()).Items.Should().BeEmpty();
    }

    [Fact]
    public void Items_are_parsed_with_all_fields()
    {
        var result = FindUnusedStepDefinitionsService.MapResult(new JObject
        {
            ["items"] = new JArray(
                Item("I press add",          "WhenIPressAdd"),
                Item("the first number is (.*)", "GivenTheFirstNumberIs")),
        });

        result.Items.Should().HaveCount(2);
        result.Items[0].BindingExpression.Should().Be("I press add");
        result.Items[0].MethodName.Should().Be("WhenIPressAdd");
        result.Items[0].ClassName.Should().Be("Steps");
        result.Items[0].SourceLine.Should().Be(7);
        result.Items[0].SourceChar.Should().Be(4);
        result.Items[1].BindingExpression.Should().Be("the first number is (.*)");
    }

    [Fact]
    public void Missing_optional_fields_default_and_non_object_entries_are_skipped()
    {
        var result = FindUnusedStepDefinitionsService.MapResult(new JObject
        {
            ["items"] = new JArray(
                new JObject { ["bindingExpression"] = "bare" }, // other fields absent
                JValue.CreateString("not an object")),
        });

        result.Items.Should().ContainSingle();
        result.Items[0].BindingExpression.Should().Be("bare");
        result.Items[0].ProjectName.Should().BeNull();
        result.Items[0].SourceLine.Should().Be(0);
    }
}
