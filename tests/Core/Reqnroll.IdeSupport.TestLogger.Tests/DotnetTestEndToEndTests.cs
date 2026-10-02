using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Reqnroll.IdeSupport.TestLogger;
using Xunit;
using Xunit.Abstractions;

namespace Reqnroll.IdeSupport.TestLogger.Tests;

/// <summary>
/// The whole logger path without an IDE: a real <c>dotnet test</c> on a real Reqnroll + MSTest project
/// (<c>tests/Core/TestLoggerFixtures/MsTestReqnroll</c>), the logger registered exactly the way Rider /
/// VS Code will (<c>--test-adapter-path</c> + <c>--logger "ReqnrollIde;Endpoint=…"</c>) and the
/// way VS's injected runsettings amount to, with this test playing the IDE on a loopback socket.
/// </summary>
/// <remarks>
/// This is the regression test issue #702 never had: it asserts that a Scenario Outline produces one
/// <c>result</c> per example row sharing a row-invariant <c>managedMethod</c>, and that Reqnroll's step
/// trace rides along in <c>stdout</c>. It builds the fixture on first use (tens of seconds; needs the
/// fixture's packages restorable — cached locally, nuget.org in CI).
/// </remarks>
[Trait("Category", "Integration")]
public class DotnetTestEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public DotnetTestEndToEndTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reqnroll.IdeSupport.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (Reqnroll.IdeSupport.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static string FixtureProject() => Path.Combine(RepoRoot(), "tests", "Core", "TestLoggerFixtures", "MsTestReqnroll", "MsTestReqnroll.Fixture.csproj");

    /// <summary>
    /// A directory holding only the logger and its own <see cref="TestReporterCommonAssemblyName"/>
    /// dependency, for <c>--test-adapter-path</c>. Named explicitly rather than "copy everything next
    /// to the logger": <c>typeof(ReqnrollIdeTestLogger).Assembly.Location</c> resolves to *this test
    /// project's own* output directory at run time (where the test host loaded it from), which also
    /// holds <c>xunit.runner.visualstudio.testadapter.dll</c> and friends — copying that wholesale
    /// would reintroduce exactly the adapter contamination this staging step exists to avoid.
    /// </summary>
    private const string TestReporterCommonAssemblyName = "Reqnroll.IdeSupport.TestReporter.Common.dll";

    private static string StageLoggerDirectory()
    {
        var source = typeof(ReqnrollIdeTestLogger).Assembly.Location;
        var sourceDir = Path.GetDirectoryName(source)!;
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-testlogger-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(source, Path.Combine(dir, Path.GetFileName(source)));
        File.Copy(Path.Combine(sourceDir, TestReporterCommonAssemblyName), Path.Combine(dir, TestReporterCommonAssemblyName));
        return dir;
    }

    [Fact]
    public async Task Dotnet_test_with_the_logger_registered_streams_one_result_per_scenario_and_per_outline_row()
    {
        var loggerDir = StageLoggerDirectory();
        var runId = "e2e-" + Guid.NewGuid().ToString("N");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var receive = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            var lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null) lines.Add(line);
            return lines;
        });

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(FixtureProject())!,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(FixtureProject());
        psi.ArgumentList.Add("--test-adapter-path");
        psi.ArgumentList.Add(loggerDir);
        psi.ArgumentList.Add("--logger");
        psi.ArgumentList.Add($"{ReqnrollIdeTestLogger.FriendlyName};{ReqnrollIdeTestLogger.EndpointParameter}={endpoint};{ReqnrollIdeTestLogger.RunIdParameter}={runId}");
        psi.ArgumentList.Add("-nologo");
        // The outer test host is itself a vstest run; don't let its environment leak into the inner one.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("VSTEST_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds));
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        _output.WriteLine(stdout);
        if (stderr.Length > 0) _output.WriteLine("STDERR: " + stderr);
        exited.Should().BeTrue("dotnet test must finish within the timeout");

        // 2 failures are expected (the deliberately failing scenario + the 5+5=11 row), so the exit
        // code is non-zero by design; the run itself must have executed.
        stdout.Should().Contain("Failed!").And.Contain("Total:     5");

        var lines = await receive.WaitAsync(TimeSpan.FromSeconds(30));
        listener.Stop();
        foreach (var line in lines) _output.WriteLine(line);

        var messages = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        messages.Select(m => m.GetProperty("type").GetString()).First().Should().Be("hello");
        messages.Select(m => m.GetProperty("type").GetString()).Last().Should().Be("runComplete");

        var hello = messages[0];
        hello.GetProperty("runId").GetString().Should().Be(runId);
        hello.GetProperty("runnerPid").GetInt32().Should().NotBe(Environment.ProcessId, "the logger runs in the runner, not in this process");
        hello.GetProperty("targetFramework").GetString().Should().StartWith(".NETCoreApp,Version=v");

        var results = messages.Where(m => m.GetProperty("type").GetString() == "result").ToList();
        results.Should().HaveCount(5, "2 scenarios + 3 outline rows");
        results.Should().OnlyContain(r => r.GetProperty("runId").GetString() == runId);
        results.Should().OnlyContain(r => r.GetProperty("source").GetString()!.EndsWith("MsTestReqnroll.Fixture.dll", StringComparison.OrdinalIgnoreCase));

        // Ordinary scenarios.
        var adding = results.Single(r => r.GetProperty("fqn").GetString()!.EndsWith(".AddingTwoNumbers"));
        adding.GetProperty("outcome").GetString().Should().Be("Passed");
        adding.GetProperty("managedType").GetString().Should().Be("ReqnrollLoggerFixture.Features.CalculatorFeature");
        adding.GetProperty("managedMethod").GetString().Should().Be("AddingTwoNumbers");
        adding.GetProperty("stdout").GetString().Should().Contain("-> done:");

        var failing = results.Single(r => r.GetProperty("fqn").GetString()!.EndsWith(".AStepInTheMiddleFails"));
        failing.GetProperty("outcome").GetString().Should().Be("Failed");
        failing.GetProperty("errorMessage").GetString().Should().Contain("deliberate failure in the middle step");
        var trace = failing.GetProperty("stdout").GetString()!;
        trace.Should().Contain("-> error: deliberate failure in the middle step");
        trace.Should().Contain("-> skipped because of previous errors");

        // The Scenario Outline: one result per Examples row, one method-level identity — issue #702's gap.
        var rows = results.Where(r => r.GetProperty("fqn").GetString()!.EndsWith(".AddingRows")).ToList();
        rows.Should().HaveCount(3);
        rows.Select(r => r.GetProperty("managedMethod").GetString()).Distinct().Should().ContainSingle()
            .Which.Should().StartWith("AddingRows(", "MSTest's ManagedMethod is signature-bearing and row-invariant");
        rows.Select(r => r.GetProperty("displayName").GetString()).Distinct().Should().HaveCount(3, "each row has its own display name");
        rows.Count(r => r.GetProperty("outcome").GetString() == "Passed").Should().Be(2);
        rows.Count(r => r.GetProperty("outcome").GetString() == "Failed").Should().Be(1);

        var complete = messages[^1];
        complete.GetProperty("executed").GetInt64().Should().Be(5);
        complete.GetProperty("aborted").GetBoolean().Should().BeFalse();
    }
}
