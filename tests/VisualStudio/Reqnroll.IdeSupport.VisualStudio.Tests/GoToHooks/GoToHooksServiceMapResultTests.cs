using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToHooks;

/// <summary>
/// Client-side mapping of a <c>reqnroll/goToHooks</c> result into a
/// <see cref="GoToHooksResult"/> (<see cref="GoToHooksService.MapResult"/>).
/// </summary>
public class GoToHooksServiceMapResultTests
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
        GoToHooksService.MapResult(null).Hooks.Should().BeEmpty();
        GoToHooksService.MapResult(JValue.CreateNull()).Hooks.Should().BeEmpty();
        GoToHooksService.MapResult(new JArray()).Hooks.Should().BeEmpty();
    }

    [Fact]
    public void A_result_without_hooks_is_empty()
    {
        GoToHooksService.MapResult(new JObject()).Hooks.Should().BeEmpty();
    }

    [Fact]
    public void Hooks_are_parsed_with_type_order_and_method()
    {
        var result = GoToHooksService.MapResult(new JObject
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
        var result = GoToHooksService.MapResult(new JObject
        {
            ["hooks"] = new JArray(
                new JObject { ["hookType"] = "BeforeStep" }, // no uri
                Hook("AfterStep", 30, "M")),
        });

        result.Hooks.Should().ContainSingle();
        result.Hooks[0].MethodName.Should().Be("M");
    }
}
