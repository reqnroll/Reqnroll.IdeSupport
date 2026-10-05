using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.GoToHooks;

/// <summary>
/// Client-side mapping of a <c>reqnroll/findHooks</c> result into a
/// <see cref="FindHooksResult"/> (<see cref="FindHooksService.MapResult"/>).
/// </summary>
public class FindHooksServiceMapResultTests
{
    private static JObject Hook(string hookType, int order, string methodName) => new()
    {
        ["uri"]        = "file:///c:/w/Hooks.cs",
        ["startLine"]  = 12,
        ["startChar"]  = 8,
        ["hookType"]   = hookType,
        ["hookOrder"]  = order,
        ["methodName"] = methodName,
    };

    [Fact]
    public void A_null_or_non_object_result_is_empty()
    {
        FindHooksService.MapResult(null).Hooks.Should().BeEmpty();
        FindHooksService.MapResult(JValue.CreateNull()).Hooks.Should().BeEmpty();
        FindHooksService.MapResult(new JArray()).Hooks.Should().BeEmpty();
    }

    [Fact]
    public void A_result_without_hooks_is_empty()
    {
        FindHooksService.MapResult(new JObject()).Hooks.Should().BeEmpty();
    }

    [Fact]
    public void Hooks_are_parsed_with_type_order_and_method()
    {
        var result = FindHooksService.MapResult(new JObject
        {
            ["hooks"] = new JArray(
                Hook("BeforeScenario", 10, "SetUp"),
                Hook("AfterScenario",  20, "TearDown")),
        });

        result.Hooks.Should().HaveCount(2);
        result.Hooks[0].HookType.Should().Be("BeforeScenario");
        result.Hooks[0].HookOrder.Should().Be(10);
        result.Hooks[0].MethodName.Should().Be("SetUp");
        result.Hooks[0].StartLine.Should().Be(12);
        result.Hooks[1].HookType.Should().Be("AfterScenario");
    }

    [Fact]
    public void A_hook_without_a_uri_is_skipped()
    {
        var result = FindHooksService.MapResult(new JObject
        {
            ["hooks"] = new JArray(
                new JObject { ["hookType"] = "BeforeStep" }, // no uri
                Hook("AfterStep", 30, "M")),
        });

        result.Hooks.Should().ContainSingle();
        result.Hooks[0].MethodName.Should().Be("M");
    }
}
