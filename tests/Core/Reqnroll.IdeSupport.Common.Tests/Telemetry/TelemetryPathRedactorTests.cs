using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

// The full rule set is exercised through the LSP server's TelemetryScrubber.RedactPaths (which delegates
// here) in TelemetryScrubberTests; these pin the Common entry point the Visual Studio host uses (#1027).
public class TelemetryPathRedactorTests
{
    [Theory]
    [InlineData(@"Could not find file 'C:\Users\alice\proj\x.cs'.", "Could not find file '<path>'.")]
    [InlineData(@"Access to \\server\share\alice\file.txt denied", "Access to <path> denied")]
    [InlineData("failed at /home/alice/proj/x.feature", "failed at <path>")]
    [InlineData("see https://example.com/a?token=secret", "see <url>")]
    [InlineData("Operation timed out", "Operation timed out")]
    public void Redacts_paths_and_urls(string message, string expected)
    {
        TelemetryPathRedactor.RedactPaths(message).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Passes_null_and_empty_through(string? message)
    {
        TelemetryPathRedactor.RedactPaths(message).Should().Be(message);
    }
}
