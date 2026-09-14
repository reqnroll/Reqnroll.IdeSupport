using System.IO;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

public class ReqnrollLogPathsTests
{
    [Fact]
    public void ResolveLogDirectory_uses_LOCALAPPDATA_on_Windows()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Microsoft Windows 10.0.22631", @"C:\Users\me\AppData\Local", @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me\AppData\Local", "Reqnroll"));
    }

    [Fact]
    public void ResolveLogDirectory_falls_back_to_home_when_LOCALAPPDATA_is_unset_on_Windows()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Microsoft Windows 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll"));
    }

    [Fact]
    public void ResolveLogDirectory_uses_Library_Logs_on_macOS()
    {
        // .NET's own Environment.SpecialFolder.LocalApplicationData resolves to ~/.local/share on
        // macOS (not ~/Library/Logs), which is why this needs its own explicit branch rather than
        // relying on the BCL folder lookup — see the class remarks.
        ReqnrollLogPaths
            .ResolveLogDirectory("Darwin 23.6.0 Darwin Kernel Version 23.6.0", null, "/Users/me")
            .Should().Be(Path.Combine("/Users/me", "Library", "Logs", "Reqnroll"));
    }

    [Fact]
    public void ResolveLogDirectory_falls_back_to_XDG_style_local_share_for_anything_else()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("Linux 6.8.0-generic", null, "/home/me")
            .Should().Be(Path.Combine("/home/me", ".local", "share", "Reqnroll"));
    }

    [Fact]
    public void ResolveLogDirectory_platform_detection_is_case_insensitive()
    {
        ReqnrollLogPaths
            .ResolveLogDirectory("WINDOWS 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll"));
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
