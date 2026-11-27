using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using ReqnrollConnector;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Hosting;

/// <summary>
/// The .NET connector is built once, for net6.0, and relies on <c>"rollForward": "LatestMajor"</c>
/// in its runtimeconfig.json to run on the newest installed runtime. These tests launch the real
/// connector process (as the LSP server does) against <c>ReqnrollBindingsFixture</c>, a Reqnroll
/// assembly built for net10.0: it references System.Runtime 10.0.0.0, which a .NET 6 runtime
/// cannot load, so discovery only succeeds when the host rolled forward.
/// </summary>
public class ConnectorRollForwardTests
{
    private const string ConnectorFileName = "reqnroll-ide-connector";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    private static string ConnectorFolder => GetMetadata("ConnectorNetOutputFolder");
    private static string FixtureAssemblyPath =>
        Path.Combine(GetMetadata("BindingsFixtureOutputFolder"), "ReqnrollBindingsFixture.dll");
    private static string ConnectorDll => Path.Combine(ConnectorFolder, ConnectorFileName + ".dll");

    public static IEnumerable<object[]> LaunchModes()
    {
        yield return ["dotnet exec"];
        // The apphost reads the same runtimeconfig.json. It is only built on Windows.
        if (OperatingSystem.IsWindows())
            yield return ["apphost"];
    }

    [Fact]
    public void Runtimeconfig_declares_net6_as_the_floor_and_rolls_forward_to_the_latest_major()
    {
        var path = Path.Combine(ConnectorFolder, ConnectorFileName + ".runtimeconfig.json");
        File.Exists(path).Should().BeTrue($"the connector build should write {path}");

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var options = json.RootElement.GetProperty("runtimeOptions");

        options.GetProperty("tfm").GetString().Should().Be("net6.0");
        options.GetProperty("rollForward").GetString().Should().Be("LatestMajor");
        var framework = options.GetProperty("framework");
        framework.GetProperty("name").GetString().Should().Be("Microsoft.NETCore.App");
        framework.GetProperty("version").GetString().Should().Be("6.0.0");
    }

    [Fact]
    public void Fixture_targets_a_newer_net_than_the_connector()
    {
        // Guards the meaning of the tests below: if the fixture ever targeted net6.0, they would
        // pass without any roll-forward.
        ReferencedSystemRuntimeMajor(FixtureAssemblyPath).Should().BeGreaterThan(6);
    }

    [Theory]
    [MemberData(nameof(LaunchModes))]
    public void Connector_discovers_bindings_from_an_assembly_built_for_a_newer_net(string launchMode)
    {
        var run = RunDiscovery(launchMode);

        run.ExitCode.Should().Be(0, run.Output);
        var result = ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(run.Output);
        result.Should().NotBeNull(run.Output);
        result!.IsFailed.Should().BeFalse(result.ErrorMessage);
        result.StepDefinitions.Select(s => s.Method).Should().Contain(m => m.Contains("CalculatorSteps"));
    }

    [Theory]
    [MemberData(nameof(LaunchModes))]
    public void Connector_runs_on_the_newest_installed_runtime(string launchMode)
    {
        var expected = NewestInstalledNetCoreRuntime();

        var run = RunDiscovery(launchMode);

        RuntimeVersionReported(run.Output).Should().Be(expected, run.Output);
    }

    [Fact]
    public void Without_roll_forward_the_same_connector_cannot_discover_that_assembly()
    {
        // The dotnet host's --roll-forward option overrides runtimeconfig.json. "Minor" is the
        // default .NET policy: it never moves to a newer major, so the connector either runs on
        // a .NET 6 runtime and cannot load System.Runtime 10.0.0.0, or (with no .NET 6 runtime
        // installed, as on CI) does not start at all. Either way it must not succeed, which
        // shows that the roll-forward setting is what the passing tests above depend on.
        var run = RunProcess(DotnetHost(), ["exec", "--roll-forward", "Minor", ConnectorDll, .. DiscoveryArguments()]);

        run.ExitCode.Should().NotBe(0, run.Output);
        run.Output.Should().NotContain(ConnectorJsonSerialization.StartMarker);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static ProcessRun RunDiscovery(string launchMode) => launchMode switch
    {
        "dotnet exec" => RunProcess(DotnetHost(), ["exec", ConnectorDll, .. DiscoveryArguments()]),
        "apphost" => RunProcess(Path.Combine(ConnectorFolder, ConnectorFileName + ".exe"), DiscoveryArguments()),
        _ => throw new ArgumentOutOfRangeException(nameof(launchMode), launchMode, null)
    };

    private static string[] DiscoveryArguments() => ["discovery", FixtureAssemblyPath, ""];

    private static string RuntimeVersionReported(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(Runner.RuntimeVersionLogPrefix));
        line.Should().NotBeNull($"the connector logs '{Runner.RuntimeVersionLogPrefix}<version>'. Output:{Environment.NewLine}{output}");
        return line![(line!.IndexOf(Runner.RuntimeVersionLogPrefix, StringComparison.Ordinal) + Runner.RuntimeVersionLogPrefix.Length)..].Trim();
    }

    /// <summary>
    /// The version LatestMajor selects: the highest installed Microsoft.NETCore.App release
    /// (prereleases are skipped unless DOTNET_ROLL_FORWARD_TO_PRERELEASE is set, which
    /// <see cref="RunProcess"/> clears).
    /// </summary>
    private static string NewestInstalledNetCoreRuntime()
    {
        var run = RunProcess(DotnetHost(), ["--list-runtimes"]);
        run.ExitCode.Should().Be(0, run.Output);

        var versions = run.Output.Split('\n')
            .Select(l => l.Trim().Split(' '))
            .Where(parts => parts.Length >= 2 && parts[0] == "Microsoft.NETCore.App")
            .Select(parts => Version.TryParse(parts[1], out var version) ? version : null)
            .Where(version => version != null)
            .ToList();
        versions.Should().NotBeEmpty(run.Output);
        return versions.Max()!.ToString();
    }

    private static int ReferencedSystemRuntimeMajor(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var systemRuntime = metadata.AssemblyReferences
            .Select(metadata.GetAssemblyReference)
            .Single(reference => metadata.GetString(reference.Name) == "System.Runtime");
        return systemRuntime.Version.Major;
    }

    /// <summary>The dotnet host running this test, so the tests and the connector see the same runtimes.</summary>
    private static string DotnetHost()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
            return hostPath;
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private static ProcessRun RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // Settings that would override runtimeconfig.json's roll-forward policy.
        startInfo.Environment.Remove("DOTNET_ROLL_FORWARD");
        startInfo.Environment.Remove("DOTNET_ROLL_FORWARD_TO_PRERELEASE");
        startInfo.Environment.Remove("DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX");

        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName}' did not exit within {ProcessTimeout}. Output:{Environment.NewLine}{output}");
        }
        process.WaitForExit(); // flushes the asynchronous output readers

        lock (output)
            return new ProcessRun(process.ExitCode, output.ToString());
    }

    private static string GetMetadata(string key) =>
        typeof(ConnectorRollForwardTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key).Value!;

    private sealed record ProcessRun(int ExitCode, string Output);
}
