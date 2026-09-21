using System.Text.Json;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

/// <summary>
/// Covers <see cref="ReqnrollMtpReporter.FormatRunComplete"/> directly — the <c>runComplete</c> NDJSON
/// line's <c>canceled</c> field must reflect <c>ITestSessionContext.CancellationToken.IsCancellationRequested</c>,
/// not the hardcoded <c>false</c> the field originally shipped with (found in review of #718).
/// </summary>
public class ReqnrollMtpReporterTests
{
    [Fact]
    public void FormatRunComplete_reports_canceled_true_when_cancellation_was_requested()
    {
        var reporter = new ReqnrollMtpReporter();

        var line = reporter.FormatRunComplete(canceled: true);

        var json = JsonDocument.Parse(line).RootElement;
        json.GetProperty("type").GetString().Should().Be("runComplete");
        json.GetProperty("canceled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void FormatRunComplete_reports_canceled_false_when_cancellation_was_not_requested()
    {
        var reporter = new ReqnrollMtpReporter();

        var line = reporter.FormatRunComplete(canceled: false);

        var json = JsonDocument.Parse(line).RootElement;
        json.GetProperty("canceled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void FormatRunComplete_always_reports_aborted_false()
    {
        // A genuine abort (host process crashed/killed) means OnTestSessionFinishingAsync never runs —
        // see FormatRunComplete's own doc comment. This locks in that "aborted" stays false rather than
        // some future edit inventing a signal that doesn't exist at this call site.
        var reporter = new ReqnrollMtpReporter();

        var line = reporter.FormatRunComplete(canceled: false);

        JsonDocument.Parse(line).RootElement.GetProperty("aborted").GetBoolean().Should().BeFalse();
    }
}
