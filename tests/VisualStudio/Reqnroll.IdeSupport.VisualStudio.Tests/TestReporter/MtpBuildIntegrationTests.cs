using System;
using System.IO;
using AwesomeAssertions;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestReporter;

/// <summary>
/// Covers <see cref="MtpBuildIntegration"/> — the user-global <c>ImportAfter</c> file-drop that
/// replaced the original <c>CustomAfterMicrosoftCommonTargets</c> environment-variable injection after
/// the latter was live-verified to never reach VS's actual build (see the type's own remarks).
/// </summary>
public sealed class MtpBuildIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-build-integration-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── BuildTargetsXml ──────────────────────────────────────────────────────

    [Fact]
    public void BuildTargetsXml_declares_the_HintPath_reference_and_the_builder_hook()
    {
        var xml = MtpBuildIntegration.BuildTargetsXml(@"C:\ext\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.dll");

        xml.Should().Contain("<Reference Include=\"Reqnroll.IdeSupport.TestReporter.MTP\">");
        xml.Should().Contain(@"<HintPath>C:\ext\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.dll</HintPath>");
        xml.Should().Contain($"<TestingPlatformBuilderHook Include=\"{MtpBuildIntegration.HookGuid}\">");
        xml.Should().Contain("<TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>");
    }

    [Fact]
    public void BuildTargetsXml_gates_the_item_group_on_the_MTP_opt_in_properties_and_the_dll_existing()
    {
        var xml = MtpBuildIntegration.BuildTargetsXml(@"C:\ext\Reqnroll.IdeSupport.TestReporter.MTP.dll");

        xml.Should().Contain("Exists('C:\\ext\\Reqnroll.IdeSupport.TestReporter.MTP.dll')");
        xml.Should().Contain("'$(IsTestProject)' == 'true'");
        xml.Should().Contain("'$(IsTestingPlatformApplication)' == 'true'");
        xml.Should().Contain("'$(EnableMSTestRunner)' == 'true'");
        xml.Should().Contain("'$(EnableNUnitRunner)' == 'true'");
        xml.Should().Contain("'$(UseMicrosoftTestingPlatformRunner)' == 'true'");
    }

    // ── ResolveImportAfterDirectory ──────────────────────────────────────────

    [Fact]
    public void ResolveImportAfterDirectory_matches_MSBuildUserExtensionsPath_ImportAfter_layout()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "MSBuild", "Current", "Microsoft.Common.targets", "ImportAfter");

        MtpBuildIntegration.ResolveImportAfterDirectory().Should().Be(expected);
    }

    // ── TryEnable ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryEnable_writes_the_targets_file_into_the_given_ImportAfter_directory()
    {
        Directory.CreateDirectory(_dir);
        var reporterDllPath = Path.Combine(_dir, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        File.WriteAllText(reporterDllPath, "not a real assembly");
        var importAfterDir = Path.Combine(_dir, "ImportAfter");

        var result = MtpBuildIntegration.TryEnable(reporterDllPath, importAfterDir, _logger);

        result.Should().NotBeNull();
        File.Exists(result!).Should().BeTrue();
        Path.GetDirectoryName(result).Should().Be(importAfterDir);
        File.ReadAllText(result!).Should().Contain(reporterDllPath);
    }

    [Fact]
    public void TryEnable_overwrites_a_previously_written_file()
    {
        Directory.CreateDirectory(_dir);
        var reporterDllPath = Path.Combine(_dir, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        File.WriteAllText(reporterDllPath, "not a real assembly");
        var importAfterDir = Path.Combine(_dir, "ImportAfter");
        var first = MtpBuildIntegration.TryEnable(reporterDllPath, importAfterDir, _logger);

        var newReporterDllPath = Path.Combine(_dir, "moved", "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(newReporterDllPath)!);
        File.WriteAllText(newReporterDllPath, "not a real assembly either");
        var second = MtpBuildIntegration.TryEnable(newReporterDllPath, importAfterDir, _logger);

        second.Should().Be(first);
        File.ReadAllText(second!).Should().Contain(newReporterDllPath);
    }

    [Fact]
    public void TryEnable_creates_the_ImportAfter_directory_if_it_does_not_exist()
    {
        Directory.CreateDirectory(_dir);
        var reporterDllPath = Path.Combine(_dir, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        File.WriteAllText(reporterDllPath, "not a real assembly");
        var importAfterDir = Path.Combine(_dir, "does", "not", "exist", "yet");

        var result = MtpBuildIntegration.TryEnable(reporterDllPath, importAfterDir, _logger);

        result.Should().NotBeNull();
        Directory.Exists(importAfterDir).Should().BeTrue();
    }

    [Fact]
    public void TryEnable_returns_null_when_the_reporter_dll_is_missing()
    {
        Directory.CreateDirectory(_dir);
        var importAfterDir = Path.Combine(_dir, "ImportAfter");

        var result = MtpBuildIntegration.TryEnable(Path.Combine(_dir, "Missing.dll"), importAfterDir, _logger);

        result.Should().BeNull();
        Directory.Exists(importAfterDir).Should().BeFalse();
    }
}
