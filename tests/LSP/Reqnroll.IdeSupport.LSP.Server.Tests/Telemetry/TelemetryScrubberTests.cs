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
    public void RedactPaths_is_idempotent()
    {
        var once = TelemetryScrubber.RedactPaths(@"Could not load C:\Users\alice\x.dll and /home/bob/y.dll");

        TelemetryScrubber.RedactPaths(once).Should().Be(once);
    }
}
