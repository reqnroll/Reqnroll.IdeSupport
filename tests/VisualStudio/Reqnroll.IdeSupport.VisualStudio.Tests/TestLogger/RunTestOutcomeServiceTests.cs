using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.TestOutcomes;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// The <c>registerRun</c> request VS sends per Test Explorer execution carries a <c>runMode</c> so the server
/// counts it as a Run/Debug request (issue #850). VS cannot tell Run from Debug, so it must say "Unknown".
/// </summary>
public class RunTestOutcomeServiceTests
{
    [Fact]
    public void RegisterRun_params_carry_runMode_Unknown_in_the_wire_shape_the_server_reads()
    {
        var parsed = JObject.Parse(RunTestOutcomeService.RegisterRunParamsJson);

        parsed.Properties().Select(p => p.Name).Should().Equal("runMode");
        parsed["runMode"]!.Value<string>().Should().Be("Unknown");
    }
}
