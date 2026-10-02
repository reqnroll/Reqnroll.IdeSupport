#nullable enable

using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class TelemetryScrubberTests
{
    [Theory]
    [InlineData(@"Error reading C:\Users\alice\project\feature.feature", @"Error reading <path>")]
    [InlineData(@"Failed at /home/bob/project/feature.feature", @"Failed at <path>")]
    [InlineData(@"UNC failure \\server\share\file.feature", @"UNC failure <path>")]
    [InlineData("No paths here", "No paths here")]
    [InlineData("", "")]
    public void RedactPaths_replaces_filesystem_paths(string message, string expected)
        => TelemetryScrubber.RedactPaths(message).Should().Be(expected);

    [Fact]
    public void ScrubProperties_also_scrubs_StackFrames_and_leaves_clean_frames_untouched()
    {
        var clean = "Reqnroll.IdeSupport.A.B.Go\n[external]\nReqnroll.IdeSupport.A.C.Run";
        var props = new Dictionary<string, object?> { [TelemetryProperties.StackFrames] = clean };
        TelemetryScrubber.ScrubProperties(props)[TelemetryProperties.StackFrames].Should().Be(clean);

        var dirty = new Dictionary<string, object?> { [TelemetryProperties.StackFrames] = @"Go at C:\Users\alice\x.cs" };
        TelemetryScrubber.ScrubProperties(dirty)[TelemetryProperties.StackFrames].Should().Be("Go at <path>");
    }

    [Fact]
    public void RedactPaths_is_idempotent()
    {
        var once = TelemetryScrubber.RedactPaths(@"Could not load C:\Users\alice\x.dll and /home/bob/y.dll");

        TelemetryScrubber.RedactPaths(once).Should().Be(once);
    }
}
