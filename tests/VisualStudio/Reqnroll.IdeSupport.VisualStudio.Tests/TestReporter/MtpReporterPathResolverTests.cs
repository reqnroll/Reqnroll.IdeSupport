using System;
using System.IO;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestReporter;

public sealed class MtpReporterPathResolverTests : IDisposable
{
    private readonly string _extensionDirectory = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-reporter-resolver-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_extensionDirectory, recursive: true); } catch { }
    }

    private string FakeExtensionAssemblyLocation => Path.Combine(_extensionDirectory, "Reqnroll.IdeSupport.VisualStudio.Extension.dll");

    [Fact]
    public void Resolve_returns_the_dll_path_when_the_bundled_reporter_is_present()
    {
        var reporterDirectory = Path.Combine(_extensionDirectory, MtpReporterPathResolver.ReporterSubdirectory);
        Directory.CreateDirectory(reporterDirectory);
        var dllPath = Path.Combine(reporterDirectory, MtpReporterPathResolver.ReporterAssemblyFileName);
        File.WriteAllText(dllPath, "not a real assembly");

        MtpReporterPathResolver.Resolve(FakeExtensionAssemblyLocation).Should().Be(dllPath);
    }

    [Fact]
    public void Resolve_returns_null_when_the_bundled_reporter_is_absent()
    {
        Directory.CreateDirectory(_extensionDirectory);

        MtpReporterPathResolver.Resolve(FakeExtensionAssemblyLocation).Should().BeNull();
    }
}
