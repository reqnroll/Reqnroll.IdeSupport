using System.IO;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class ReqnrollLogPathsTests
{
    [Fact]
    public void ResolveApplicationDirectory_uses_LOCALAPPDATA_on_Windows()
    {
        ReqnrollLogPaths
            .ResolveApplicationDirectory("Microsoft Windows 10.0.22631", @"C:\Users\me\AppData\Local", @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me\AppData\Local", "Reqnroll"));
    }

    [Fact]
    public void ResolveApplicationDirectory_falls_back_to_home_when_LOCALAPPDATA_is_unset_on_Windows()
    {
        ReqnrollLogPaths
            .ResolveApplicationDirectory("Microsoft Windows 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll"));
    }

    [Fact]
    public void ResolveApplicationDirectory_uses_Library_Logs_on_macOS()
    {
        // .NET's own Environment.SpecialFolder.LocalApplicationData resolves to ~/.local/share on
        // macOS (not ~/Library/Logs), which is why this needs its own explicit branch rather than
        // relying on the BCL folder lookup — see the class remarks.
        ReqnrollLogPaths
            .ResolveApplicationDirectory("Darwin 23.6.0 Darwin Kernel Version 23.6.0", null, "/Users/me")
            .Should().Be(Path.Combine("/Users/me", "Library", "Logs", "Reqnroll"));
    }

    [Fact]
    public void ResolveApplicationDirectory_falls_back_to_XDG_style_local_share_for_anything_else()
    {
        ReqnrollLogPaths
            .ResolveApplicationDirectory("Linux 6.8.0-generic", null, "/home/me")
            .Should().Be(Path.Combine("/home/me", ".local", "share", "Reqnroll"));
    }

    [Fact]
    public void ResolveApplicationDirectory_platform_detection_is_case_insensitive()
    {
        ReqnrollLogPaths
            .ResolveApplicationDirectory("WINDOWS 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll"));
    }

    [Fact]
    public void ResolveLogDirectory_is_a_logs_subfolder_of_the_application_directory_on_Windows()
    {
        // Issue #726: log files must not sit directly alongside unrelated persisted state
        // (test-outcomes.json, the telemetry userid file) in the application directory's root.
        ReqnrollLogPaths
            .ResolveLogDirectory("Microsoft Windows 10.0.22631", @"C:\Users\me\AppData\Local", @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me\AppData\Local", "Reqnroll", "logs"));
    }

    [Fact]
    public void ResolveLogDirectory_falls_back_to_home_when_LOCALAPPDATA_is_unset_on_Windows()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Microsoft Windows 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll", "logs"));
    }

    [Fact]
    public void ResolveLogDirectory_uses_Library_Logs_on_macOS()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Darwin 23.6.0 Darwin Kernel Version 23.6.0", null, "/Users/me")
            .Should().Be(Path.Combine("/Users/me", "Library", "Logs", "Reqnroll", "logs"));
    }

    [Fact]
    public void ResolveLogDirectory_falls_back_to_XDG_style_local_share_for_anything_else()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Linux 6.8.0-generic", null, "/home/me")
            .Should().Be(Path.Combine("/home/me", ".local", "share", "Reqnroll", "logs"));
    }

    [Fact]
    public void ResolveLogDirectory_platform_detection_is_case_insensitive()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("WINDOWS 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll", "logs"));
    }

    [Fact]
    public void ResolveLogDirectory_live_call_is_a_logs_subfolder_of_ResolveApplicationDirectory()
    {
        // Smoke test against the real environment (not injected values): the two public,
        // no-argument entry points must keep agreeing with each other regardless of which OS this
        // actually runs on, since every caller that isn't testing the per-OS branches directly
        // goes through these rather than the internal pure overloads above.
        Path.Combine(ReqnrollLogPaths.ResolveApplicationDirectory(), "logs")
            .Should().Be(ReqnrollLogPaths.ResolveLogDirectory());
    }

    [Fact]
    public void PruneOldLogFiles_deletes_only_stale_reqnroll_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reqnroll-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, "reqnroll-vs-ext-20200101-1.log");
            var fresh = Path.Combine(dir, "reqnroll-vs-ext-20990101-2.log");
            var unrelated = Path.Combine(dir, "not-ours.log");
            File.WriteAllText(stale, "old");
            File.WriteAllText(fresh, "new");
            File.WriteAllText(unrelated, "ignore me");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));

            ReqnrollLogPaths.PruneOldLogFiles(dir);

            File.Exists(stale).Should().BeFalse("older than the 10-day retention window");
            File.Exists(fresh).Should().BeTrue("recently written files must be kept");
            File.Exists(unrelated).Should().BeTrue("only reqnroll-* files are ours to prune");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PruneOldLogFiles_never_throws_for_a_missing_directory()
    {
        var act = () => ReqnrollLogPaths.PruneOldLogFiles(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}"));

        act.Should().NotThrow();
    }
}
