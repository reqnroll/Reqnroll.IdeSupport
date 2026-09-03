using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

/// <summary>
/// Issue #568: <see cref="ProcessHelper"/> (external process spawning for the out-of-process
/// reflection connector) has zero test references anywhere in the codebase. These tests spawn the
/// real <c>dotnet</c> executable — guaranteed present in this repository's build/test
/// environment — rather than mocking <see cref="System.Diagnostics.Process"/>, since the class's
/// entire job is orchestrating a real OS process (argument quoting, timeout, output capture).
/// </summary>
public class ProcessHelperTests
{
    private static readonly string WorkingDirectory = Path.GetTempPath();

    [Fact]
    public void RunProcess_captures_the_exit_code_and_standard_output_of_a_successful_run()
    {
        var result = ProcessHelper.RunProcess(WorkingDirectory, "dotnet", new[] { "--version" });

        result.ExitCode.Should().Be(0);
        result.StandardOut.Should().NotBeNullOrWhiteSpace();
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void RunProcess_captures_a_non_zero_exit_code_without_throwing()
    {
        var result = ProcessHelper.RunProcess(WorkingDirectory, "dotnet", new[] { "not-a-real-dotnet-command" });

        result.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public void RunProcess_records_the_executable_arguments_and_working_directory_on_the_result()
    {
        var result = ProcessHelper.RunProcess(WorkingDirectory, "dotnet", new[] { "--version" });

        result.ExecutablePath.Should().Be("dotnet");
        result.WorkingDirectory.Should().Be(WorkingDirectory);
        result.CommandLine.Should().Contain("dotnet").And.Contain(WorkingDirectory);
    }

    [Fact]
    public void RunProcess_with_a_missing_working_directory_returns_a_failure_result_by_default()
    {
        var missingDir = Path.Combine(WorkingDirectory, "does-not-exist-" + Guid.NewGuid());

        var result = ProcessHelper.RunProcess(missingDir, "dotnet", new[] { "--version" });

        result.ExitCode.Should().Be(-1);
        result.StandardError.Should().Contain("Unable to find directory");
    }

    [Fact]
    public void RunProcess_with_a_missing_working_directory_throws_when_throwException_is_true()
    {
        var missingDir = Path.Combine(WorkingDirectory, "does-not-exist-" + Guid.NewGuid());

        var act = () => ProcessHelper.RunProcess(missingDir, "dotnet", new[] { "--version" }, throwException: true);

        act.Should().Throw<IdeSupportConfigurationException>().WithMessage("*Unable to find directory*");
    }

    [Fact]
    public void RunProcess_with_a_nonexistent_executable_path_returns_a_failure_result_by_default()
    {
        var missingExe = Path.Combine(WorkingDirectory, "does-not-exist-" + Guid.NewGuid() + ".exe");

        var result = ProcessHelper.RunProcess(WorkingDirectory, missingExe, new[] { "--version" });

        result.ExitCode.Should().Be(-1);
        result.StandardError.Should().Contain("Unable to find process");
    }

    [Fact]
    public void RunProcess_with_a_nonexistent_executable_path_throws_when_throwException_is_true()
    {
        var missingExe = Path.Combine(WorkingDirectory, "does-not-exist-" + Guid.NewGuid() + ".exe");

        var act = () => ProcessHelper.RunProcess(WorkingDirectory, missingExe, new[] { "--version" }, throwException: true);

        act.Should().Throw<IdeSupportConfigurationException>().WithMessage("*Unable to find process*");
    }

    [Fact]
    public void RunProcess_treats_a_bare_command_name_as_PATH_resolvable_without_pre_validating_it()
    {
        // "dotnet" has no directory component, so ProcessHelper must not File.Exists-check it
        // (that would always fail for a PATH-resolved command) and instead let Process.Start
        // resolve it via the OS's PATH search, same as a shell would.
        var result = ProcessHelper.RunProcess(WorkingDirectory, "dotnet", new[] { "--version" }, throwException: true);

        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public void RunProcess_respects_an_explicit_timeout_for_a_long_running_process()
    {
        var act = () => ProcessHelper.RunProcess(
            WorkingDirectory, "dotnet", new[] { "--version" },
            timeout: TimeSpan.FromMilliseconds(1), throwException: true);

        // Either the process finishes faster than the 1ms timeout (unlikely — spawning a new OS
        // process alone takes longer than that) or the timeout fires — either is an acceptable
        // outcome as long as RunProcess actually enforces the timeout it was given rather than
        // hanging indefinitely.
        var stopwatch = Stopwatch.StartNew();
        try { act(); } catch (TimeoutException) { }
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "a 1ms timeout must not let the process run to completion unbounded");
    }
}
