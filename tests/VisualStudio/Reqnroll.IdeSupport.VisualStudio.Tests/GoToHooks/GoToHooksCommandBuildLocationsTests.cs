using System.Collections.Generic;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToHooks;

/// <summary>
/// Maps applicable hooks onto Find All References window rows
/// (<see cref="HookLocationsMapper.BuildLocations"/>), used when Go To Hooks resolves several
/// hooks at the caret instead of showing the NavigationPickerDialog modal popup (issue #315).
/// </summary>
public class GoToHooksCommandBuildLocationsTests
{
    private static HookLocation Hook(string hookType, string methodName, int startLine = 12, int startChar = 8) =>
        new("file:///c:/w/Hooks.cs", startLine, startChar, hookType, hookOrder: 10, methodName);

    [Fact]
    public void Carries_the_hook_s_source_position_through_unchanged()
    {
        var locations = HookLocationsMapper.BuildLocations(new List<HookLocation> { Hook("BeforeScenario", "SetUp", startLine: 12, startChar: 8) });

        var location = locations.Should().ContainSingle().Subject;
        location.FileUri.Should().Be("file:///c:/w/Hooks.cs");
        location.StartLine.Should().Be(12);
        location.StartChar.Should().Be(8);
        location.EndLine.Should().Be(12);
        location.EndChar.Should().Be(8);
    }

    [Fact]
    public void Labels_the_code_column_with_the_hook_type_and_method_name()
    {
        var locations = HookLocationsMapper.BuildLocations(new List<HookLocation> { Hook("AfterScenario", "TearDown") });

        locations.Should().ContainSingle().Which.StepText.Should().Be("[AfterScenario] TearDown");
    }

    [Fact]
    public void Preserves_the_server_returned_order_for_several_hooks()
    {
        var locations = HookLocationsMapper.BuildLocations(new List<HookLocation>
        {
            Hook("BeforeScenario", "First"),
            Hook("BeforeScenario", "Second"),
            Hook("AfterScenario",  "Third"),
        });

        locations.Should().HaveCount(3);
        locations[0].StepText.Should().Be("[BeforeScenario] First");
        locations[1].StepText.Should().Be("[BeforeScenario] Second");
        locations[2].StepText.Should().Be("[AfterScenario] Third");
    }
}
