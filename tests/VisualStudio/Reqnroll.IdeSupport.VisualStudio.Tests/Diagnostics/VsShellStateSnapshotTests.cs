using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.Diagnostics;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Diagnostics;

/// <summary>
/// Covers how <see cref="VsShellStateSnapshot"/> interprets a shell-state reading taken when a
/// shutdown token fires (issue #555).
/// </summary>
/// <remarks>
/// The distinction under test is the whole point of the diagnostic: an unreadable property must
/// never be reported as evidence that the shell is staying up, because that is the reading that
/// would send someone chasing a spurious-cancellation bug that isn't there.
/// </remarks>
public class VsShellStateSnapshotTests
{
    private static VsShellStateSnapshot WithShutdownStarted(bool? shutdownStarted) =>
        new(ShellInitialized: true,
            ShellShutdownStarted: shutdownStarted,
            SolutionOpen: true,
            SolutionClosing: false,
            SolutionFileName: @"C:\repo\Minimal.sln",
            ProbeError: null);

    [Fact]
    public void Contradicts_shutdown_when_the_shell_says_it_is_not_shutting_down()
    {
        WithShutdownStarted(false).ContradictsShutdown.Should().BeTrue();
    }

    [Fact]
    public void Does_not_contradict_shutdown_when_the_shell_confirms_it_is_shutting_down()
    {
        WithShutdownStarted(true).ContradictsShutdown.Should().BeFalse();
    }

    [Fact]
    public void Does_not_contradict_shutdown_when_the_property_could_not_be_read()
    {
        WithShutdownStarted(null).ContradictsShutdown.Should().BeFalse();
    }

    [Fact]
    public void Failed_probe_contradicts_nothing_and_says_why()
    {
        var snapshot = VsShellStateSnapshot.Failed("UI thread did not respond within 2000ms");

        snapshot.ContradictsShutdown.Should().BeFalse();
        snapshot.Describe().Should().Be("shell state unavailable (UI thread did not respond within 2000ms)");
    }

    [Fact]
    public void Describe_reports_every_field_it_read()
    {
        WithShutdownStarted(false).Describe().Should().Be(
            "shellInitialized=True, shellShutdownStarted=False, solutionOpen=True, " +
            @"solutionClosing=False, solutionFile=C:\repo\Minimal.sln");
    }

    [Fact]
    public void Describe_marks_unread_properties_as_unknown_rather_than_guessing()
    {
        var snapshot = new VsShellStateSnapshot(
            ShellInitialized: null,
            ShellShutdownStarted: null,
            SolutionOpen: null,
            SolutionClosing: null,
            SolutionFileName: null,
            ProbeError: null);

        snapshot.Describe().Should().Be(
            "shellInitialized=unknown, shellShutdownStarted=unknown, solutionOpen=unknown, " +
            "solutionClosing=unknown, solutionFile=(none)");
    }
}
