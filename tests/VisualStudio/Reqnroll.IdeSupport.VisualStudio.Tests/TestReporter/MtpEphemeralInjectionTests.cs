using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestReporter;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestReporter;

public sealed class MtpEphemeralInjectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-ephemeral-injection-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── EnumerateProjectFiles ────────────────────────────────────────────────

    [Fact]
    public void EnumerateProjectFiles_finds_csproj_files_at_any_depth()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src", "Nested"));
        File.WriteAllText(Path.Combine(_dir, "Root.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(_dir, "src", "Nested", "Nested.csproj"), string.Empty);

        var found = MtpEphemeralInjection.EnumerateProjectFiles(_dir).Select(Path.GetFileName).ToList();

        found.Should().BeEquivalentTo("Root.csproj", "Nested.csproj");
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData(".vs")]
    [InlineData("node_modules")]
    public void EnumerateProjectFiles_prunes_excluded_directories(string excludedDirectoryName)
    {
        Directory.CreateDirectory(Path.Combine(_dir, excludedDirectoryName));
        File.WriteAllText(Path.Combine(_dir, excludedDirectoryName, "Inside.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(_dir, "Root.csproj"), string.Empty);

        var found = MtpEphemeralInjection.EnumerateProjectFiles(_dir).Select(Path.GetFileName).ToList();

        found.Should().BeEquivalentTo("Root.csproj");
    }

    [Fact]
    public void EnumerateProjectFiles_returns_nothing_for_a_directory_with_no_csproj_files()
    {
        Directory.CreateDirectory(_dir);

        MtpEphemeralInjection.EnumerateProjectFiles(_dir).Should().BeEmpty();
    }

    // ── WriteTargetsFile ─────────────────────────────────────────────────────

    [Fact]
    public void WriteTargetsFile_declares_the_HintPath_reference_and_the_builder_hook()
    {
        var file = MtpEphemeralInjection.WriteTargetsFile(@"C:\ext\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.dll", preExistingCustomAfterTargets: null);

        try
        {
            var xml = File.ReadAllText(file);
            xml.Should().Contain("<Reference Include=\"Reqnroll.IdeSupport.TestReporter.MTP\">");
            xml.Should().Contain(@"<HintPath>C:\ext\MtpReporter\Reqnroll.IdeSupport.TestReporter.MTP.dll</HintPath>");
            xml.Should().Contain($"<TestingPlatformBuilderHook Include=\"{MtpEphemeralInjection.HookGuid}\">");
            xml.Should().Contain("<TypeFullName>Reqnroll.IdeSupport.TestReporter.MTP.TestingPlatformBuilderHook</TypeFullName>");
            xml.Should().NotContain("<Import ");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public void WriteTargetsFile_chain_imports_a_pre_existing_CustomAfterMicrosoftCommonTargets_value()
    {
        var file = MtpEphemeralInjection.WriteTargetsFile(@"C:\ext\Reqnroll.IdeSupport.TestReporter.MTP.dll", @"C:\other\SomeOther.targets");

        try
        {
            var xml = File.ReadAllText(file);
            xml.Should().Contain(@"<Import Project=""C:\other\SomeOther.targets"" Condition=""Exists('C:\other\SomeOther.targets')"" />");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    // ── TryEnableForSolution ─────────────────────────────────────────────────

    [Fact]
    public void TryEnableForSolution_sets_the_environment_variable_when_an_MTP_capable_project_exists()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Tests.csproj"),
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>");
        var reporterDllPath = Path.Combine(_dir, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        File.WriteAllText(reporterDllPath, "not a real assembly");
        var original = Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, null, EnvironmentVariableTarget.Process);

            var result = MtpEphemeralInjection.TryEnableForSolution(_dir, reporterDllPath, _logger);

            result.Should().BeTrue();
            var value = Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process);
            value.Should().NotBeNullOrEmpty();
            File.Exists(value).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, original, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void TryEnableForSolution_does_not_set_the_environment_variable_when_no_project_is_MTP_capable()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Tests.csproj"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var reporterDllPath = Path.Combine(_dir, "Reqnroll.IdeSupport.TestReporter.MTP.dll");
        File.WriteAllText(reporterDllPath, "not a real assembly");
        var original = Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, null, EnvironmentVariableTarget.Process);

            var result = MtpEphemeralInjection.TryEnableForSolution(_dir, reporterDllPath, _logger);

            result.Should().BeFalse();
            Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, original, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void TryEnableForSolution_does_not_set_the_environment_variable_when_the_reporter_dll_is_missing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Tests.csproj"),
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>");
        var original = Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, null, EnvironmentVariableTarget.Process);

            var result = MtpEphemeralInjection.TryEnableForSolution(_dir, Path.Combine(_dir, "Missing.dll"), _logger);

            result.Should().BeFalse();
            Environment.GetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, EnvironmentVariableTarget.Process).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MtpEphemeralInjection.CustomAfterMicrosoftCommonTargetsVariable, original, EnvironmentVariableTarget.Process);
        }
    }
}
