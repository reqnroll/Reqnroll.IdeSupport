using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;
using Xunit;

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
            packageReferences = Array.Empty<object>()
        });
    }
}
