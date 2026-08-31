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
/// project always re-runs binding discovery, whether or not the output assembly path or target
/// framework changed. This covers both the path-change case the output-assembly file watcher can
/// miss, and a plain rebuild (issue #542): Visual Studio re-sends projectLoaded after every
/// successful build with identical OutputAssemblyPath/TFM, and that re-send is its only rebuild
/// signal because it doesn't register the watcher at all. The assembly-hash guard downstream in
/// ConnectorDiscoveryService makes a redundant trigger a cheap no-op when nothing actually changed.
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
    public async Task Reloading_with_unchanged_inputs_still_triggers_rediscovery()
    {
        var outputPath = Path.Combine(_root, "bin", "Debug", "Proj.dll");
        await LoadInitialProjectWithProviderAsync(outputPath);
        var signal = ArmDiscoverySignal();

        // Identical notification, e.g. VsProjectEventMonitor.OnBuildDone re-sending
        // projectLoaded after a plain rebuild that didn't move the output path or TFM.
        await _manager.HandleProjectLoadedAsync(Params(outputPath), CancellationToken.None);

        (await WaitForDiscoveryAsync(signal, 4000)).Should().BeTrue(
            "a rebuild must re-run discovery even when the output path/TFM are unchanged (issue #542), " +
            "since Visual Studio's OnBuildDone re-send is the only rebuild signal it has");
    }
}
