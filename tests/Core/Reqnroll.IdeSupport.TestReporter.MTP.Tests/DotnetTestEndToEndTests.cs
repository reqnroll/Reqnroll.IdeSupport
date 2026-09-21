using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

/// <summary>
/// The whole MTP reporter path without an IDE: a real <c>dotnet test</c> (native MTP mode, via the
/// fixture's own scoped <c>global.json</c>) against a real Reqnroll + MSTest project
/// (<c>tests/Core/TestReporterFixtures/MsTestReqnrollMtp</c>) with the reporter compiled in, discovering
/// this test's loopback listener through a hand-written session breadcrumb file — exactly the discovery
/// path <c>SessionBreadcrumbMatcher</c>/<c>WorkspaceRootLocator</c> implement, with this test playing
/// the LSP server's role on both sides (the breadcrumb writer and the listener).
/// </summary>
/// <remarks>
/// This is issue #715 Phase 2's own exit criterion: a live run showing per-row Scenario Outline
/// outcomes for an MTP-mode Reqnroll fixture project, over the same wire protocol the VSTest logger's
/// <c>DotnetTestEndToEndTests</c> (Reqnroll.IdeSupport.TestLogger.Tests) already proves for VSTest.
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

    private static string FixtureDirectory() => Path.Combine(RepoRoot(), "tests", "Core", "TestReporterFixtures", "MsTestReqnrollMtp");

    /// <summary>Writes a session breadcrumb (the shape <c>TestOutcomeSessionBreadcrumb</c> writes) into a hermetic temp directory, naming this repo's own root — the same root <see cref="WorkspaceRootLocator"/> resolves from the fixture's build output.</summary>
    private static string StageSessionsDirectory(string endpoint)
    {
        var dir = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-reporter-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            endpoint,
            workspaceRoot = RepoRoot(),
            lspServerPid = 999999,
            startedUtc = DateTime.UtcNow,
        });
        File.WriteAllText(Path.Combine(dir, "999999.json"), json);
        return dir;
    }

    [Fact]
    public async Task Dotnet_test_in_MTP_mode_with_a_matching_breadcrumb_streams_one_result_per_scenario_and_per_outline_row()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var sessionsDir = StageSessionsDirectory(endpoint);

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
            WorkingDirectory = FixtureDirectory(),
        };
        psi.ArgumentList.Add("test");
        psi.Environment[SessionsDirectory.OverrideEnvironmentVariable] = sessionsDir;
        // The outer test host is itself a testing-platform run; don't let its environment leak into the inner one.
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

        // 2 failures are expected (the deliberately failing scenario + the 5+5=11 row).
        stdout.Should().Contain("total: 5").And.Contain("failed: 2");

        var lines = await receive.WaitAsync(TimeSpan.FromSeconds(30));
        listener.Stop();
        foreach (var line in lines) _output.WriteLine(line);

        var messages = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        messages.Select(m => m.GetProperty("type").GetString()).First().Should().Be("hello");
        messages.Select(m => m.GetProperty("type").GetString()).Last().Should().Be("runComplete");

        var hello = messages[0];
        hello.GetProperty("connected").GetBoolean().Should().BeTrue();
        hello.GetProperty("runnerPid").GetInt32().Should().NotBe(Environment.ProcessId, "the reporter runs in the MTP test host, not in this process");

        var results = messages.Where(m => m.GetProperty("type").GetString() == "result").ToList();
        results.Should().HaveCount(5, "2 scenarios + 3 outline rows");
        results.Should().OnlyContain(r => r.GetProperty("source").GetString()!.EndsWith("MsTestReqnrollMtp.Fixture.dll", StringComparison.OrdinalIgnoreCase));

        // Ordinary scenarios.
        var adding = results.Single(r => r.GetProperty("fqn").GetString()!.EndsWith(".AddingTwoNumbers"));
        adding.GetProperty("outcome").GetString().Should().Be("Passed");
        adding.GetProperty("managedType").GetString().Should().Be("ReqnrollMtpReporterFixture.Features.CalculatorFeature");
        adding.GetProperty("managedMethod").GetString().Should().StartWith("AddingTwoNumbers(");

        var failing = results.Single(r => r.GetProperty("fqn").GetString()!.EndsWith(".AStepInTheMiddleFails"));
        failing.GetProperty("outcome").GetString().Should().Be("Failed");
        failing.GetProperty("errorMessage").GetString().Should().Contain("deliberate failure in the middle step");

        // The Scenario Outline: one result per Examples row, one method-level identity.
        var rows = results.Where(r => r.GetProperty("fqn").GetString()!.EndsWith(".AddingRows")).ToList();
        rows.Should().HaveCount(3);
        rows.Select(r => r.GetProperty("managedMethod").GetString()).Distinct().Should().ContainSingle()
            .Which.Should().StartWith("AddingRows(", "the MTP identity is signature-bearing and row-invariant");
        rows.Select(r => r.GetProperty("displayName").GetString()).Distinct().Should().HaveCount(3, "each row has its own display name");
        rows.Count(r => r.GetProperty("outcome").GetString() == "Passed").Should().Be(2);
        rows.Count(r => r.GetProperty("outcome").GetString() == "Failed").Should().Be(1);

        var complete = messages[^1];
        complete.GetProperty("executed").GetInt32().Should().Be(5);
    }
}
