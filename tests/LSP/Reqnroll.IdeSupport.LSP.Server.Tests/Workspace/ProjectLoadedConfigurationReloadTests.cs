using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Verifies that a <c>reqnroll/projectLoaded</c> re-send for an already-known project picks up
/// configuration-file edits. Clients that never send <c>workspace/didChangeWatchedFiles</c>
/// (Visual Studio) re-send projectLoaded after each build, which is their only chance to
/// notice that <c>reqnroll.json</c> changed.
/// </summary>
public class ProjectLoadedConfigurationReloadTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly LspWorkspaceScopeManager _manager;
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _configPath;

    public ProjectLoadedConfigurationReloadTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "reqnroll.json");
        _manager = new LspWorkspaceScopeManager(new LspIdeScope(_logger), _logger, _mediator);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ReqnrollProjectLoadedParams Params() => new()
    {
        WorkspaceFolder        = _root,
        ProjectFile            = Path.Combine(_root, "Proj.csproj"),
        ProjectFolder          = _root,
        OutputAssemblyPath     = Path.Combine(_root, "bin", "Debug", "Proj.dll"),
        TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
    };

    private async Task<LspReqnrollProject> LoadProjectAsync()
    {
        LspReqnrollProject? captured = null;
        _manager.ProjectDiscovered += p => captured = p;
        await _manager.HandleProjectLoadedAsync(Params(), CancellationToken.None);
        captured.Should().NotBeNull();
        return captured!;
    }

    private async Task<bool> ConfigChangedPublishedAsync(LspReqnrollProject project)
    {
        for (var i = 0; i < 80; i++)
        {
            if (_mediator.ReceivedCalls().Any(c =>
                    c.GetArguments().FirstOrDefault() is ReqnrollConfigChangedNotification n &&
                    ReferenceEquals(n.Project, project)))
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task A_resend_after_reqnroll_json_appears_reloads_the_configuration_and_publishes_a_change()
    {
        var project = await LoadProjectAsync();
        project.GetIdeSupportConfiguration().DefaultFeatureLanguage.Should().NotBe("fr");

        File.WriteAllText(_configPath, "{ \"language\": { \"feature\": \"fr\" } }");
        await _manager.HandleProjectLoadedAsync(Params(), CancellationToken.None);

        project.GetIdeSupportConfiguration().DefaultFeatureLanguage.Should().Be("fr");
        (await ConfigChangedPublishedAsync(project)).Should().BeTrue(
            "open feature files must be re-parsed with the new language");
    }

    [Fact]
    public async Task A_resend_with_unchanged_configuration_publishes_nothing()
    {
        File.WriteAllText(_configPath, "{ \"language\": { \"feature\": \"fr\" } }");
        var project = await LoadProjectAsync();
        _ = project.GetIdeSupportConfiguration();

        await _manager.HandleProjectLoadedAsync(Params(), CancellationToken.None);

        (await ConfigChangedPublishedAsync(project)).Should().BeFalse();
    }
}
