using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RenameStep;

/// <summary>
/// Client-side mapping of a <c>reqnroll/renameTargets</c> JSON result into a
/// <see cref="RenameTargetsResult"/> (<see cref="RenameStepService.MapTargets"/>), used by the
/// multi-attribute rename picker.
/// </summary>
public class RenameTargetsMappingTests
{
    private static JObject Target(string label, string expression, int attributeIndex) => new()
    {
        ["label"]          = label,
        ["expression"]     = expression,
        ["attributeIndex"] = attributeIndex,
    };

    [Fact]
    public void A_non_object_result_returns_null()
    {
        RenameStepService.MapTargets(JValue.CreateNull()).Should().BeNull();
        RenameStepService.MapTargets(new JArray()).Should().BeNull();
    }

    [Fact]
    public void A_result_without_targets_returns_null()
    {
        RenameStepService.MapTargets(new JObject()).Should().BeNull();
    }

    [Fact]
    public void An_empty_targets_array_returns_null()
    {
        RenameStepService.MapTargets(new JObject { ["targets"] = new JArray() }).Should().BeNull();
    }

    [Fact]
    public void Targets_are_mapped_with_label_expression_and_attribute_index()
    {
        var result = RenameStepService.MapTargets(new JObject
        {
            ["targets"] = new JArray(
                Target("When I press add",  "I press add",  0),
                Target("When I invoke add", "I invoke add", 2)),
        });

        result.Should().NotBeNull();
        result!.Targets.Should().HaveCount(2);
        result.Targets[0].Label.Should().Be("When I press add");
        result.Targets[0].Expression.Should().Be("I press add");
        result.Targets[0].AttributeIndex.Should().Be(0);
        result.Targets[1].Expression.Should().Be("I invoke add");
        result.Targets[1].AttributeIndex.Should().Be(2);
    }

    [Fact]
    public void Missing_fields_default_and_non_object_entries_are_skipped()
    {
        var result = RenameStepService.MapTargets(new JObject
        {
            ["targets"] = new JArray(
                new JObject { ["expression"] = "only expression" }, // label/index default
                JValue.CreateString("not an object")),
        });

        result.Should().NotBeNull();
        result!.Targets.Should().ContainSingle();
        result.Targets[0].Expression.Should().Be("only expression");
        result.Targets[0].Label.Should().BeEmpty();
        result.Targets[0].AttributeIndex.Should().Be(0);
    }
}
