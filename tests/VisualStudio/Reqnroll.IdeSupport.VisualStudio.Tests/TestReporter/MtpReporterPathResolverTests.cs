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
    private string BundleDirectory => Path.Combine(_extensionDirectory, MtpReporterPathResolver.ReporterSubdirectory);

    [Fact]
    public void Resolve_returns_the_bundle_targets_path_when_the_targets_and_sources_are_present()
    {
        Directory.CreateDirectory(Path.Combine(BundleDirectory, MtpReporterPathResolver.SourceSubdirectory));
        var targets = Path.Combine(BundleDirectory, MtpReporterPathResolver.BundleTargetsFileName);
        File.WriteAllText(targets, "<Project />");

        MtpReporterPathResolver.Resolve(FakeExtensionAssemblyLocation).Should().Be(targets);
    }

    [Fact]
    public void Resolve_returns_null_when_the_bundle_is_absent()
    {
        Directory.CreateDirectory(_extensionDirectory);

        MtpReporterPathResolver.Resolve(FakeExtensionAssemblyLocation).Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_for_an_incomplete_bundle_without_its_sources()
    {
        Directory.CreateDirectory(BundleDirectory);
        File.WriteAllText(Path.Combine(BundleDirectory, MtpReporterPathResolver.BundleTargetsFileName), "<Project />");

        MtpReporterPathResolver.Resolve(FakeExtensionAssemblyLocation).Should().BeNull();
    }
}
