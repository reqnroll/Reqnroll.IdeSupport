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

    // Adversarial shapes (issue #621): spaces in the user directory, token URLs, relative paths, bare file
    // names, home/env-var forms, two paths in one message, and ordinary text that must survive.
    [Theory]
    [InlineData(@"EACCES C:\Users\John Smith\My Projects\Calc\a.feature denied", "EACCES <path> denied")]
    [InlineData(@"\\srv\my share\dir\f failed", "<path> failed")]
    [InlineData("open /home/john smith/work/x now", "open <path> now")]
    [InlineData(@"ENOENT C:\Users\bob\a.feature and \\srv\share\x and /home/bob/b.cs failed", "ENOENT <path> and <path> and <path> failed")]
    [InlineData("fetch https://example.com/a?token=SECRET123#frag failed", "fetch <url> failed")]
    [InlineData("file:///c:/Users/bob/x.json", "<url>")]
    [InlineData("missing src/Features/Calc.feature and ../shared/x", "missing <path> and <path>")]
    [InlineData("Error in Calculator.feature, Steps.cs and App.csproj", "Error in <path>, <path> and <path>")]
    [InlineData(@"x %USERPROFILE%\proj\a here", "x <path> here")]
    [InlineData("x ~/work/proj/a here", "x <path> here")]
    [InlineData("x $HOME/work/a here", "x <path> here")]
    [InlineData("and/or textDocument/definition System.Text.Json x", "and/or textDocument/definition System.Text.Json x")]
    public void RedactPaths_handles_adversarial_shapes(string message, string expected)
        => TelemetryScrubber.RedactPaths(message).Should().Be(expected);

    [Fact]
    public void RedactPaths_is_idempotent()
    {
        var once = TelemetryScrubber.RedactPaths(@"Could not load C:\Users\alice\x.dll and /home/bob/y.dll");

        TelemetryScrubber.RedactPaths(once).Should().Be(once);
    }
}
