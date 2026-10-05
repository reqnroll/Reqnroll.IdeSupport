using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.GoToHooks;

/// <summary>
/// The client-originated "GoToHook command executed" event carries the closed <c>Source</c>
/// property, shared with VS Code and Rider (issue #861).
/// </summary>
public class GoToHookTelemetryTests
{
    [Fact]
    public void Event_is_named_after_the_catalog_constant_and_carries_Source_as_the_only_property()
    {
        var evt = GoToHookTelemetry.CreateEvent(GoToHookSources.ContextMenu);

        evt.EventName.Should().Be("GoToHook command executed");
        evt.Properties.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object>("Source", "ContextMenu"));
    }

    [Fact]
    public void Source_values_match_the_closed_enum_shared_with_the_other_IDEs()
    {
        GoToHookSources.PropertyName.Should().Be("Source");
        new[] { GoToHookSources.Command, GoToHookSources.ContextMenu, GoToHookSources.CodeLens }
            .Should().Equal("Command", "ContextMenu", "CodeLens");
    }
}
