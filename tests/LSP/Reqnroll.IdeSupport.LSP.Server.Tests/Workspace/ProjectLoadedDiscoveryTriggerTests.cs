using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Verifies that a <c>reqnroll/projectLoaded</c> notification for an <em>already-known</em>
/// project re-runs binding discovery when (and only when) the output assembly path or target
/// framework changed — covering the path-change case the output-assembly file watcher can miss.
/// </summary>
public class ProjectLoadedDiscoveryTriggerTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IConnectorDiscoveryService _discovery = Substitute.For<IConnectorDiscoveryService>();
    private readonly LspIdeScope _ideScope;
    private readonly LspWorkspaceScopeManager _manager;
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ProjectLoadedDiscoveryTriggerTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _manager = new LspWorkspaceScopeManager(_ideScope, _logger, _mediator);
        _discovery.RunDiscovery(
                Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProjectBindingRegistry.Invalid, string.Empty));
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ReqnrollProjectLoadedParams Params(string outputAssemblyPath, string tfm = ".NETCoreApp,Version=v8.0")
        => new()
        {
            WorkspaceFolder        = _root,
            ProjectFile            = Path.Combine(_root, "Proj.csproj"),
            ProjectFolder          = _root,
            OutputAssemblyPath     = outputAssemblyPath,
            TargetFrameworkMoniker = tfm
        };

    /// <summary>
    /// Loads the project once, then injects a test-controlled binding provider into its
    /// property bag (the role normally played by BindingRegistryProviderRouter) so we can
    /// observe whether a subsequent load triggers a discovery run.
    /// </summary>
    private async Task<LspReqnrollProject> LoadInitialProjectWithProviderAsync(string initialOutputPath)
    {
        LspReqnrollProject? captured = null;
        _manager.ProjectDiscovered += p => captured = p;

        await _manager.HandleProjectLoadedAsync(Params(initialOutputPath), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Properties[typeof(ConnectorBindingRegistryProvider)] =
            new ConnectorBindingRegistryProvider(captured, _discovery, _logger);
        return captured;
    }

    private async Task<bool> WaitForDiscoveryAsync(TaskCompletionSource signal, int timeoutMs)
    {
        var completed = await Task.WhenAny(signal.Task, Task.Delay(timeoutMs));
        return completed == signal.Task;
    }

    private TaskCompletionSource ArmDiscoverySignal()
    {
        var signal = new TaskCompletionSource();
        _discovery.When(d => d.RunDiscovery(
                Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => signal.TrySetResult());
        return signal;
    }

    [Fact]
    public async Task Reloading_with_a_changed_output_path_triggers_rediscovery()
    {
        var project = await LoadInitialProjectWithProviderAsync(
            Path.Combine(_root, "bin", "Debug", "Proj.dll"));
        var signal = ArmDiscoverySignal();

        // Same project file, different output path (e.g. Debug → Release).
        await _manager.HandleProjectLoadedAsync(
            Params(Path.Combine(_root, "bin", "Release", "Proj.dll")), CancellationToken.None);

        (await WaitForDiscoveryAsync(signal, 4000)).Should().BeTrue(
            "an output-path change must re-run binding discovery");
        _ = project;
    }

    [Fact]
    public async Task Reloading_with_a_changed_target_framework_triggers_rediscovery()
    {
        await LoadInitialProjectWithProviderAsync(Path.Combine(_root, "bin", "Debug", "Proj.dll"));
        var signal = ArmDiscoverySignal();

        await _manager.HandleProjectLoadedAsync(
            Params(Path.Combine(_root, "bin", "Debug", "Proj.dll"), tfm: ".NETCoreApp,Version=v9.0"),
            CancellationToken.None);

        (await WaitForDiscoveryAsync(signal, 4000)).Should().BeTrue();
    }

    [Fact]
    public async Task Reloading_with_unchanged_inputs_does_not_trigger_rediscovery()
    {
        var outputPath = Path.Combine(_root, "bin", "Debug", "Proj.dll");
        await LoadInitialProjectWithProviderAsync(outputPath);
        var signal = ArmDiscoverySignal();

        // Identical notification — nothing relevant changed.
        await _manager.HandleProjectLoadedAsync(Params(outputPath), CancellationToken.None);

        (await WaitForDiscoveryAsync(signal, 1200)).Should().BeFalse(
            "an unchanged reload must not re-run discovery");
    }
}
