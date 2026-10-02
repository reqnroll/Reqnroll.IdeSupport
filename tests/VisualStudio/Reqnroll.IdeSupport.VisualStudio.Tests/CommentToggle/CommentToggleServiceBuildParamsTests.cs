using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;

namespace Reqnroll.VisualStudio.Tests.CommentToggle;

/// <summary>
/// Pins the <c>workspace/executeCommand</c> payload <see cref="CommentToggleService"/> sends for
/// <c>reqnroll.toggleComment</c>: the server's <c>CommentToggleHandler</c> reads the mode from the
/// fourth argument and only accepts <c>toggle</c>/<c>comment</c>/<c>uncomment</c> (issue #747).
/// </summary>
public class CommentToggleServiceBuildParamsTests
{
    [Theory]
    [InlineData(CommentToggleMode.Toggle,    "toggle")]
    [InlineData(CommentToggleMode.Comment,   "comment")]
    [InlineData(CommentToggleMode.Uncomment, "uncomment")]
    public void Sends_uri_line_range_and_mode_as_the_command_arguments(CommentToggleMode mode, string expectedWireMode)
    {
        var json = JObject.Parse(
            CommentToggleService.BuildParams("file:///c:/repo/My%20Feature.feature", 2, 5, mode));

        json["command"]!.Value<string>().Should().Be("reqnroll.toggleComment");
        var args = (JArray)json["arguments"]!;
        args.Should().HaveCount(4);
        args[0].Value<string>().Should().Be("file:///c:/repo/My%20Feature.feature");
        args[1].Value<int>().Should().Be(2);
        args[2].Value<int>().Should().Be(5);
        args[3].Value<string>().Should().Be(expectedWireMode);
    }
}
