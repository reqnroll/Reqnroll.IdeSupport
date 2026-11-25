using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;
using Xunit;
using Reqnroll.IdeSupport.LSP.Core.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Announces a project whose output assembly is the prebuilt <c>ReqnrollBindingsFixture</c>, so the
/// server runs the <em>real</em> out-of-process connector as part of handling
/// <c>reqnroll/projectLoaded</c>.
/// <para>
/// This is what lets a scenario exercise the seam between the two discovery sources. Every other
/// spec gets its bindings from the Roslyn live path (an opened <c>.cs</c>), and the Discovery specs
/// run the connector standalone without a server — so the merge rule between them ("Roslyn-derived
/// bindings for a file replace previous entries for that file; connector output replaces the entire
/// registry") was never exercised as a sequence.
/// </para>
/// </summary>
[Binding]
public sealed class ConnectorDiscoverySteps
{
    /// <summary>The fixture assembly targets net10.0, and the connector is chosen by TFM.</summary>
    private const string FixtureTargetFrameworkMoniker = ".NETCoreApp,Version=v10.0";

    private readonly LspScenarioContext _ctx;

    public ConnectorDiscoverySteps(LspScenarioContext ctx) => _ctx = ctx;

    [When("the project is announced with the prebuilt bindings fixture")]
    public async Task WhenTheProjectIsAnnouncedWithTheFixture()
    {
        Skip.IfNot(FixtureDiscovery.IsAvailable,
            "The connector binaries and/or the ReqnrollBindingsFixture assembly were not deployed " +
            "next to the test host; build the solution so the specs' DeployDiscoveryAssets target runs.");

        await _ctx.EnsureStartedAsync().ConfigureAwait(false);

        // The project folder stays the scenario's workspace so .cs and .feature files written by
        // other steps belong to it; only the output assembly points at the fixture.
        var project = _ctx.RegisterProject(
            LspScenarioContext.DefaultProjectFileName,
            targetFrameworkMoniker: FixtureTargetFrameworkMoniker);

        _ctx.Harness.Client.SendProjectLoaded(new
        {
            workspaceFolder = _ctx.WorkspaceFolder,
            projectFile = project.ProjectFile,
            projectFolder = project.ProjectFolder,
            outputAssemblyPath = FixtureDiscovery.FixtureAssemblyPath,
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            // The fixture is a Reqnroll project and has to say so: only its own assembly is
            // deployed next to the test host, so the output-folder fallback in
            // ReqnrollProjectDetector cannot see a Reqnroll runtime there.
            packageReferences = new object[]
            {
                new { packageId = "Reqnroll", version = "2.1.0", installPath = "" }
            }
        });
    }

    /// <summary>
    /// Announces a Reqnroll project whose output assembly exists but is not a valid assembly, so
    /// the real out-of-process connector starts and fails to load it.
    /// </summary>
    [When("the project is announced with an output assembly the connector cannot load")]
    public async Task WhenTheProjectIsAnnouncedWithAnUnloadableAssembly()
    {
        Skip.IfNot(FixtureDiscovery.IsAvailable,
            "The connector binaries were not deployed next to the test host; build the solution so " +
            "the specs' DeployDiscoveryAssets target runs.");

        await _ctx.EnsureStartedAsync().ConfigureAwait(false);

        var project = _ctx.RegisterProject(
            LspScenarioContext.DefaultProjectFileName,
            targetFrameworkMoniker: FixtureTargetFrameworkMoniker);

        var assemblyPath = Path.Combine(project.ProjectFolder, "bin", "Debug", "net10.0", "Sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        await File.WriteAllTextAsync(assemblyPath, "this is not an assembly").ConfigureAwait(false);

        _ctx.Harness.Client.SendProjectLoaded(new
        {
            workspaceFolder = _ctx.WorkspaceFolder,
            projectFile = project.ProjectFile,
            projectFolder = project.ProjectFolder,
            outputAssemblyPath = assemblyPath,
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            packageReferences = new object[]
            {
                new { packageId = "Reqnroll", version = "2.1.0", installPath = "" }
            }
        });
    }

    [Then(@"a warning containing ""([^""]*)"" is sent to the Reqnroll output pane within (\d+) seconds")]
    public async Task ThenAWarningIsSentToTheOutputPane(string text, int seconds)
    {
        // MessageType.Warning in the LSP LogMessageParams shape.
        const int warning = 2;
        var sent = await _ctx.Harness.WaitForReqnrollLogMessageAsync(
                (type, message) => type == warning && message.Contains(text, StringComparison.Ordinal),
                seconds * 1000)
            .ConfigureAwait(false);

        Assert.True(sent,
            $"expected a reqnroll/logMessage warning containing '{text}'; received: " +
            string.Join(" | ", _ctx.Harness.ReqnrollLogMessages.Select(m => $"[{m.Type}] {m.Message}")));
    }
}
