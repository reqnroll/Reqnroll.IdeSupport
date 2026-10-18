using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Utilities;

/// <summary>
/// Covers the "was this file saved a moment ago?" decision that keeps a format rejection arriving from
/// the save sequence out of the dialog (issue #1132).
/// </summary>
public class RecentSaveTrackerTests
{
    private const string Path = @"C:\repo\Features\Price.feature";

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void A_file_that_was_never_saved_is_not_recent()
    {
        new RecentSaveTracker().SavedRecently(Path).Should().BeFalse();
    }

    [Fact]
    public void A_save_that_just_began_is_recent()
    {
        var clock = new Clock();
        var tracker = new RecentSaveTracker(() => clock.Now);

        tracker.SaveBegan(Path);

        tracker.SavedRecently(Path).Should().BeTrue();
    }

    [Fact]
    public void It_stays_recent_for_the_window_and_not_after()
    {
        var clock = new Clock();
        var tracker = new RecentSaveTracker(() => clock.Now);
        tracker.SaveBegan(Path);

        clock.Now += RecentSaveTracker.DefaultWindow;
        tracker.SavedRecently(Path).Should().BeTrue("exactly at the window edge");

        clock.Now += TimeSpan.FromMilliseconds(1);
        tracker.SavedRecently(Path).Should().BeFalse("just past the window");
    }

    [Fact]
    public void Another_file_is_unaffected_and_paths_compare_case_insensitively()
    {
        var tracker = new RecentSaveTracker();
        tracker.SaveBegan(Path);

        tracker.SavedRecently(@"C:\repo\Features\Other.feature").Should().BeFalse();
        tracker.SavedRecently(Path.ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public void A_later_save_restarts_the_window()
    {
        var clock = new Clock();
        var tracker = new RecentSaveTracker(() => clock.Now);
        tracker.SaveBegan(Path);
        clock.Now += TimeSpan.FromSeconds(2.5);
        tracker.SaveBegan(Path);
        clock.Now += TimeSpan.FromSeconds(2.5);

        tracker.SavedRecently(Path).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_file_without_a_path_is_never_recent_and_is_ignored(string? path)
    {
        var tracker = new RecentSaveTracker();

        tracker.SaveBegan(path!);

        tracker.SavedRecently(path!).Should().BeFalse();
    }
}
