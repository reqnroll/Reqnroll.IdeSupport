using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.TestTargets;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestTargets;

/// <summary>
/// Client-side mapping of a <c>reqnroll/resolveTestTargets</c> result into
/// <see cref="ScenarioTestTarget"/>s (<see cref="ScenarioTestTargetService.MapResult"/>).
/// </summary>
public class ScenarioTestTargetServiceMapResultTests
{
    private static JObject Target(string type, string method, bool isParameterized = false, int? rowIndex = null) => new()
    {
        ["declaringTypeFullName"] = type,
        ["methodName"] = method,
        ["isParameterized"] = isParameterized,
        ["rowIndex"] = rowIndex,
    };

    [Fact]
    public void A_null_or_non_object_result_is_empty()
    {
        ScenarioTestTargetService.MapResult(null).Should().BeEmpty();
    }

    [Fact]
    public void A_result_without_a_targets_array_is_empty()
    {
        ScenarioTestTargetService.MapResult(new JObject()).Should().BeEmpty();
    }

    [Fact]
    public void A_single_non_parameterized_target_is_parsed()
    {
        var result = ScenarioTestTargetService.MapResult(new JObject
        {
            ["targets"] = new JArray(Target("Tests.FFeature", "AddTwoNumbers")),
        });

        result.Should().ContainSingle();
        result[0].DeclaringTypeFullName.Should().Be("Tests.FFeature");
        result[0].MethodName.Should().Be("AddTwoNumbers");
        result[0].IsParameterized.Should().BeFalse();
        result[0].RowIndex.Should().BeNull();
    }

    [Fact]
    public void Parameterized_row_tests_targets_carry_a_row_index()
    {
        var result = ScenarioTestTargetService.MapResult(new JObject
        {
            ["targets"] = new JArray(
                Target("Tests.FFeature", "AddNumbers", isParameterized: true, rowIndex: 0),
                Target("Tests.FFeature", "AddNumbers", isParameterized: true, rowIndex: 1)),
        });

        result.Should().HaveCount(2);
        result[0].IsParameterized.Should().BeTrue();
        result[0].RowIndex.Should().Be(0);
        result[1].RowIndex.Should().Be(1);
    }

    [Fact]
    public void A_target_missing_declaringTypeFullName_or_methodName_is_skipped()
    {
        var result = ScenarioTestTargetService.MapResult(new JObject
        {
            ["targets"] = new JArray(
                new JObject { ["methodName"] = "M" }, // no declaringTypeFullName
                new JObject { ["declaringTypeFullName"] = "Tests.FFeature" }, // no methodName
                Target("Tests.FFeature", "Ok")),
        });

        result.Should().ContainSingle();
        result[0].MethodName.Should().Be("Ok");
    }
}
