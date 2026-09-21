using Reqnroll.IdeSupport.TestReporter.MTP;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

public class SessionsDirectoryTests
{
    [Fact]
    public void Resolve_uses_LOCALAPPDATA_on_Windows()
        => SessionsDirectory.Resolve("Microsoft Windows 10.0.22631", @"C:\Users\me\AppData\Local", @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me\AppData\Local", "Reqnroll", "test-outcomes", "sessions"));

    [Fact]
    public void Resolve_falls_back_to_home_when_LOCALAPPDATA_is_unset_on_Windows()
        => SessionsDirectory.Resolve("Microsoft Windows 10.0.22631", null, @"C:\Users\me")
            .Should().Be(Path.Combine(@"C:\Users\me", "Reqnroll", "test-outcomes", "sessions"));

    [Fact]
    public void Resolve_uses_Library_Logs_on_macOS()
        => SessionsDirectory.Resolve("Darwin 23.6.0 Darwin Kernel Version 23.6.0", null, "/Users/me")
            .Should().Be(Path.Combine("/Users/me", "Library", "Logs", "Reqnroll", "test-outcomes", "sessions"));

    [Fact]
    public void Resolve_falls_back_to_XDG_style_local_share_for_anything_else()
        => SessionsDirectory.Resolve("Linux 6.8.0-generic", null, "/home/me")
            .Should().Be(Path.Combine("/home/me", ".local", "share", "Reqnroll", "test-outcomes", "sessions"));
}
