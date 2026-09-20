using System;
using System.IO;
using AwesomeAssertions;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.TestLogger;

/// <summary>
/// Covers <see cref="TestLoggerActivationRules"/>, the pure decision logic extracted from
/// <see cref="ReqnrollTestLoggerRunSettingsService"/> specifically so it could be unit tested without
/// loading any <c>Microsoft.VisualStudio.TestWindow.*</c> assembly -- see that class's remarks.
/// <see cref="ReqnrollTestLoggerRunSettingsService"/> itself (and its <c>AddRunSettings</c>/
/// <c>RegisterRunBlocking</c>) stays uncovered: constructing it at all forces the type loader to
/// resolve those VS-install-supplied assemblies, which aren't present in a standalone test run.
/// </summary>
public sealed class TestLoggerActivationRulesTests : IDisposable
{
    private readonly string? _originalDisableValue =
        Environment.GetEnvironmentVariable(TestLoggerActivationRules.DisableEnvironmentVariable);

    public void Dispose()
        => Environment.SetEnvironmentVariable(TestLoggerActivationRules.DisableEnvironmentVariable, _originalDisableValue);

    // ── IsDisabled ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)] // any other non-empty value is treated as "disabled" -- fail safe, not fail open
    public void IsDisabled_reflects_the_environment_variable(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable(TestLoggerActivationRules.DisableEnvironmentVariable, value);

        TestLoggerActivationRules.IsDisabled().Should().Be(expected);
    }

    // ── IsReqnrollTestContainer ─────────────────────────────────────────────────

    [Fact]
    public void IsReqnrollTestContainer_is_true_when_Reqnroll_dll_sits_beside_the_container()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-activation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Reqnroll.dll"), "not a real assembly");
            var source = Path.Combine(dir, "Specs.dll");

            TestLoggerActivationRules.IsReqnrollTestContainer(source, Substitute.For<IIdeSupportLogger>()).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsReqnrollTestContainer_is_false_when_Reqnroll_dll_is_absent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-activation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "Specs.dll");

            TestLoggerActivationRules.IsReqnrollTestContainer(source, Substitute.For<IIdeSupportLogger>()).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── ResolveLoggerDirectory ──────────────────────────────────────────────────

    [Fact]
    public void ResolveLoggerDirectory_returns_the_directory_when_the_bundled_logger_is_present()
    {
        var extensionDirectory = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-activation-tests", Guid.NewGuid().ToString("N"));
        var loggerDirectory = Path.Combine(extensionDirectory, TestLoggerRunSettings.LoggerSubdirectory);
        Directory.CreateDirectory(loggerDirectory);
        try
        {
            File.WriteAllText(Path.Combine(loggerDirectory, TestLoggerRunSettings.LoggerAssemblyFileName), "not a real assembly");
            var fakeExtensionAssemblyLocation = Path.Combine(extensionDirectory, "Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration.dll");

            TestLoggerActivationRules.ResolveLoggerDirectory(fakeExtensionAssemblyLocation).Should().Be(loggerDirectory);
        }
        finally
        {
            Directory.Delete(extensionDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveLoggerDirectory_returns_null_when_the_bundled_logger_is_absent()
    {
        var extensionDirectory = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-activation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extensionDirectory);
        try
        {
            var fakeExtensionAssemblyLocation = Path.Combine(extensionDirectory, "Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration.dll");

            TestLoggerActivationRules.ResolveLoggerDirectory(fakeExtensionAssemblyLocation).Should().BeNull();
        }
        finally
        {
            Directory.Delete(extensionDirectory, recursive: true);
        }
    }
}
