using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToStepDefinition;

/// <summary>
/// The client-originated "GoToStepDefinition command executed" event (issue #898): the genuine navigation,
/// as opposed to the server's per-lookup "FindStepDefinitions command executed".
/// </summary>
public class GoToStepDefinitionTelemetryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Event_is_named_after_the_catalog_constant_and_carries_LocationCount_as_the_only_property(int count)
    {
        var evt = GoToStepDefinitionTelemetry.CreateEvent(count);

        evt.EventName.Should().Be("GoToStepDefinition command executed");
        evt.Properties.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, object>("LocationCount", count));
    }
}
