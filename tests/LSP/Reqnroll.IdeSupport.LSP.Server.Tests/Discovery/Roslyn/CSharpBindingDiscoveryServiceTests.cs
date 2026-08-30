using System.Collections.Immutable;
using System.Diagnostics;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Roslyn;

/// <summary>
/// Unit tests for <see cref="CSharpBindingDiscoveryService"/>.
/// Verifies the fan-out to N owning projects (Q17 W3a) and the I2 unowned-gate.
/// </summary>
public class CSharpBindingDiscoveryServiceTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger           _logger       = Substitute.For<IIdeSupportLogger>();
    private readonly IIdeSupportLogger           _ideLogger    = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope               _ideScope;

    private readonly string _root1 = Path.Combine(Path.GetTempPath(), "CsBDSTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _root2 = Path.Combine(Path.GetTempPath(), "CsBDSTests_" + Guid.NewGuid().ToString("N"));

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath("/workspace/src/StepDefinitions.cs");

    public CSharpBindingDiscoveryServiceTests()
    {
        _ideScope = new LspIdeScope(_ideLogger);
        Directory.CreateDirectory(_root1);
        Directory.CreateDirectory(_root2);
    }

    public void Dispose()
    {
        // LspIdeScope is not IDisposable; no cleanup needed for it.
        try { if (Directory.Exists(_root1)) Directory.Delete(_root1, recursive: true); } catch { }
        try { if (Directory.Exists(_root2)) Directory.Delete(_root2, recursive: true); } catch { }
    }

    private CSharpBindingDiscoveryService CreateSut() => new(_scopeManager, _logger);

    // Non-empty source with a real binding: BindingRegistryChanged is only raised when a
    // binding's matched expression actually changes, so an empty-to-empty "update" (the
    // registry's starting state) would never fire it.
    private const string StepDefinitionSource = @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""a step"")]
        public void Method() { }
    }
}";

    private CSharpBindingDiscoveryService CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_scopeManager, _logger, telemetry);

    // ── I2 unowned gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFromSourceAsync_logs_I2_skip_when_file_is_unowned()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(Array.Empty<LspReqnrollProject>());
        _scopeManager.GetMembershipState(Arg.Any<DocumentUri>())
            .Returns(MembershipState.Unowned);

        await CreateSut().UpdateFromSourceAsync(CsUri, string.Empty, false, CancellationToken.None);

        // LogVerbose is a static extension method — verify via the underlying Log() call.
        _logger.Received().Log(Arg.Is<LogMessage>(m =>
            m.Level == TraceLevel.Verbose &&
            m.Message.Contains("excluded") && m.Message.Contains("I2")));
    }

    [Fact]
    public async Task UpdateFromSourceAsync_logs_skip_when_file_is_pending()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(Array.Empty<LspReqnrollProject>());
        _scopeManager.GetMembershipState(Arg.Any<DocumentUri>())
            .Returns(MembershipState.Pending);

        await CreateSut().UpdateFromSourceAsync(CsUri, string.Empty, false, CancellationToken.None);

        _logger.Received().Log(Arg.Is<LogMessage>(m =>
            m.Level == TraceLevel.Verbose &&
            (m.Message.Contains("No project owns") || m.Message.Contains("state=Pending"))));
    }

    [Fact]
    public async Task UpdateFromSourceAsync_logs_skip_when_project_has_no_provider()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        // No ConnectorBindingRegistryProvider in project.Properties.
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(new[] { project });

        await CreateSut().UpdateFromSourceAsync(CsUri, string.Empty, false, CancellationToken.None);

        _logger.Received().Log(Arg.Is<LogMessage>(m =>
            m.Level == TraceLevel.Verbose && m.Message.Contains("no binding provider")));

        project.Dispose();
    }

    // ── Fan-out to N owners ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFromSourceAsync_fans_out_to_all_owning_projects()
    {
        // Two projects, each with a real ConnectorBindingRegistryProvider.
        var project1 = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var project2 = DiscoveryTestSupport.MakeProject(_ideScope, _root2);

        var provider1 = new ConnectorBindingRegistryProvider(project1, _logger);
        var provider2 = new ConnectorBindingRegistryProvider(project2, _logger);
        project1.Properties[typeof(ConnectorBindingRegistryProvider)] = provider1;
        project2.Properties[typeof(ConnectorBindingRegistryProvider)] = provider2;

        var changed1 = false;
        var changed2 = false;
        provider1.BindingRegistryChanged += (_, _) => changed1 = true;
        provider2.BindingRegistryChanged += (_, _) => changed2 = true;

        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(new[] { project1, project2 });

        // A URI that GetFileSystemPath() returns a non-empty value for.
        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        await CreateSut().UpdateFromSourceAsync(csUri, StepDefinitionSource, false, CancellationToken.None);

        changed1.Should().BeTrue("provider1 should be updated for project1");
        changed2.Should().BeTrue("provider2 should be updated for project2");

        provider1.Dispose();
        provider2.Dispose();
        project1.Dispose();
        project2.Dispose();
    }

    // ── UpdateFromSourceForProjectAsync (index-bypassing, startup reconciliation) ─

    [Fact]
    public async Task UpdateFromSourceForProjectAsync_applies_to_given_project_without_resolving_owners()
    {
        var project  = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;

        var changed = false;
        provider.BindingRegistryChanged += (_, _) => changed = true;

        var csPath = Path.Combine(_root1, "Steps.cs");

        await CreateSut().UpdateFromSourceForProjectAsync(project, csPath, StepDefinitionSource, CancellationToken.None);

        changed.Should().BeTrue("the known project's provider should be patched directly");
        // The whole point of this overload: it never consults the membership index.
        _scopeManager.DidNotReceive().ResolveOwners(Arg.Any<DocumentUri>());

        provider.Dispose();
        project.Dispose();
    }

    // Issue #471: BindingRegistryChangedHandler.RediscoverCsFilesAsync passes notify: false because
    // its own caller already reparses every open feature file and notifies unconditionally right
    // after RediscoverCsFilesAsync returns -- a second independent event here just duplicated that
    // reparse. The registry must still be patched; only the event is suppressed.
    [Fact]
    public async Task UpdateFromSourceForProjectAsync_still_applies_but_suppresses_the_event_when_notify_is_false()
    {
        var project  = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;

        var changed = false;
        provider.BindingRegistryChanged += (_, _) => changed = true;

        var csPath = Path.Combine(_root1, "Steps.cs");

        await CreateSut().UpdateFromSourceForProjectAsync(
            project, csPath, StepDefinitionSource, CancellationToken.None, notify: false);

        changed.Should().BeFalse("notify: false must suppress BindingRegistryChanged");
        provider.Current.StepDefinitions.Should().ContainSingle("the registry must still be patched regardless of notify");

        provider.Dispose();
        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceForProjectAsync_noops_when_project_has_no_provider()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        // No ConnectorBindingRegistryProvider in project.Properties.

        var csPath = Path.Combine(_root1, "Steps.cs");

        // Must not throw, and should log the "no binding provider" skip.
        await CreateSut().UpdateFromSourceForProjectAsync(project, csPath, string.Empty, CancellationToken.None);

        _logger.Received().Log(Arg.Is<LogMessage>(m =>
            m.Level == TraceLevel.Verbose && m.Message.Contains("no binding provider")));

        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceForProjectAsync_noops_on_empty_path()
    {
        var project  = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;

        var changed = false;
        provider.BindingRegistryChanged += (_, _) => changed = true;

        await CreateSut().UpdateFromSourceForProjectAsync(project, string.Empty, string.Empty, CancellationToken.None);

        changed.Should().BeFalse("an empty file path is a no-op");

        provider.Dispose();
        project.Dispose();
    }

    // ── Fan-out (continued) ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFromSourceAsync_with_single_owner_updates_only_that_provider()
    {
        var project1 = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var project2 = DiscoveryTestSupport.MakeProject(_ideScope, _root2);

        var provider1 = new ConnectorBindingRegistryProvider(project1, _logger);
        var provider2 = new ConnectorBindingRegistryProvider(project2, _logger);
        project1.Properties[typeof(ConnectorBindingRegistryProvider)] = provider1;
        project2.Properties[typeof(ConnectorBindingRegistryProvider)] = provider2;

        var changed1 = false;
        var changed2 = false;
        provider1.BindingRegistryChanged += (_, _) => changed1 = true;
        provider2.BindingRegistryChanged += (_, _) => changed2 = true;

        // Only project1 owns the file.
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(new[] { project1 });

        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        await CreateSut().UpdateFromSourceAsync(csUri, StepDefinitionSource, false, CancellationToken.None);

        changed1.Should().BeTrue("project1's provider should be updated");
        changed2.Should().BeFalse("project2's provider must not be updated — it doesn't own the file");

        provider1.Dispose();
        provider2.Dispose();
        project1.Dispose();
        project2.Dispose();
    }

    // ── Telemetry ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFromSourceAsync_emits_roslyn_discovery_telemetry()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(new[] { project });

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);
        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        await sut.UpdateFromSourceAsync(csUri, "class C {}", false, CancellationToken.None);

        telemetry.Received(1).SendEvent(
            "Reqnroll Discovery executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                "Roslyn".Equals(d["DiscoverySource"]) &&
                "csEdit".Equals(d["TriggerContext"]) &&
                false.Equals(d["IsFailed"])));
        provider.Dispose();
        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceAsync_sets_csOpen_trigger_when_isOpen_true()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(new[] { project });

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);
        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        await sut.UpdateFromSourceAsync(csUri, "class C {}", true, CancellationToken.None);

        telemetry.Received(1).SendEvent(
            "Reqnroll Discovery executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                "Roslyn".Equals(d["DiscoverySource"]) &&
                "csOpen".Equals(d["TriggerContext"])));
        provider.Dispose();
        project.Dispose();
    }

    // ── isOpen gated on HasSuccessfulConnectorRun (issue #471) ────────────────────

    [Fact]
    public async Task UpdateFromSourceAsync_skips_the_parse_on_didOpen_once_this_file_was_already_reconciled()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var discovery = Substitute.For<IConnectorDiscoveryService>();
        discovery.RunDiscovery(Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((new ProjectBindingRegistry(
                ImmutableArray<ProjectStepDefinitionBinding>.Empty, ImmutableArray<ProjectHookBinding>.Empty, projectHash: 1), "hash-1"));
        var provider = new ConnectorBindingRegistryProvider(project, discovery, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(new[] { project });

        // Drive a real, successful connector run first.
        var connectorDone = new TaskCompletionSource();
        provider.BindingRegistryChanged += (_, _) => connectorDone.TrySetResult();
        provider.TriggerRefresh();
        await Task.WhenAny(connectorDone.Task, Task.Delay(5000));
        provider.HasSuccessfulConnectorRun.Should().BeTrue("test setup: the connector run must have landed before asserting the skip");

        var csPath = Path.Combine(_root1, "Steps.cs");
        var csUri = DocumentUri.FromFileSystemPath(csPath);

        // Simulate BindingRegistryChangedHandler.RediscoverCsFilesAsync having already reconciled
        // this exact file (with this exact buffer text) as part of that startup reconciliation pass.
        var file = FileDetails.FromPath(csPath).WithCSharpContent(StepDefinitionSource);
        await provider.ApplyRoslynFileUpdateAsync(file, notify: false);
        provider.Current.StepDefinitions.Should().ContainSingle(
            "test setup: the file must already be reconciled before asserting the didOpen skip");

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);

        await sut.UpdateFromSourceAsync(csUri, StepDefinitionSource, isOpen: true, CancellationToken.None);

        provider.Current.StepDefinitions.Should().ContainSingle(
            "the file was already reconciled; a redundant didOpen parse must not run");
        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);

        provider.Dispose();
        project.Dispose();
    }

    // Issue #517: a didOpen arriving after RediscoverCsFilesAsync's snapshot (but before the user's
    // first edit) must still parse the file, even though the project already has a successful
    // connector run -- HasSuccessfulConnectorRun alone proves nothing about *this* file.
    [Fact]
    public async Task UpdateFromSourceAsync_still_parses_on_didOpen_when_this_file_was_never_reconciled()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var discovery = Substitute.For<IConnectorDiscoveryService>();
        discovery.RunDiscovery(Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((new ProjectBindingRegistry(
                ImmutableArray<ProjectStepDefinitionBinding>.Empty, ImmutableArray<ProjectHookBinding>.Empty, projectHash: 1), "hash-1"));
        var provider = new ConnectorBindingRegistryProvider(project, discovery, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(new[] { project });

        // Drive a real, successful connector run -- for the project as a whole -- but this
        // particular file's didOpen arrived too late for RediscoverCsFilesAsync's snapshot, so it
        // was never reconciled: the registry has nothing for it.
        var connectorDone = new TaskCompletionSource();
        provider.BindingRegistryChanged += (_, _) => connectorDone.TrySetResult();
        provider.TriggerRefresh();
        await Task.WhenAny(connectorDone.Task, Task.Delay(5000));
        provider.HasSuccessfulConnectorRun.Should().BeTrue("test setup: the connector run must have landed first");
        provider.Current.StepDefinitions.Should().BeEmpty("test setup: this file must not have been reconciled yet");

        var sut = CreateSut();
        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        await sut.UpdateFromSourceAsync(csUri, StepDefinitionSource, isOpen: true, CancellationToken.None);

        provider.Current.StepDefinitions.Should().ContainSingle(
            "the file was never reconciled, so didOpen must still parse it despite the project's successful connector run");

        provider.Dispose();
        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceAsync_still_applies_on_didChange_even_after_a_successful_connector_run()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var discovery = Substitute.For<IConnectorDiscoveryService>();
        discovery.RunDiscovery(Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((new ProjectBindingRegistry(
                ImmutableArray<ProjectStepDefinitionBinding>.Empty, ImmutableArray<ProjectHookBinding>.Empty, projectHash: 1), "hash-1"));
        var provider = new ConnectorBindingRegistryProvider(project, discovery, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(new[] { project });

        var connectorDone = new TaskCompletionSource();
        provider.BindingRegistryChanged += (_, _) => connectorDone.TrySetResult();
        provider.TriggerRefresh();
        await Task.WhenAny(connectorDone.Task, Task.Delay(5000));
        provider.HasSuccessfulConnectorRun.Should().BeTrue("test setup: the connector run must have landed before asserting didChange still applies");

        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));

        // isOpen: false -- an edit, not an open -- must always apply regardless of connector status.
        await CreateSut().UpdateFromSourceAsync(csUri, StepDefinitionSource, isOpen: false, CancellationToken.None);

        provider.Current.StepDefinitions.Should().ContainSingle("a live edit must always be reflected, even once the connector has already run");

        provider.Dispose();
        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceAsync_applies_on_didOpen_before_the_connector_has_ever_succeeded()
    {
        // The VS Code steady state (issue #471): no compiled DLL yet, so
        // HasSuccessfulConnectorRun is false and didOpen must still be the source of bindings.
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _root1);
        var provider = new ConnectorBindingRegistryProvider(project, _logger);
        project.Properties[typeof(ConnectorBindingRegistryProvider)] = provider;
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(new[] { project });

        provider.HasSuccessfulConnectorRun.Should().BeFalse();

        var csUri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "Steps.cs"));
        await CreateSut().UpdateFromSourceAsync(csUri, StepDefinitionSource, isOpen: true, CancellationToken.None);

        provider.Current.StepDefinitions.Should().ContainSingle();

        provider.Dispose();
        project.Dispose();
    }

    [Fact]
    public async Task UpdateFromSourceAsync_skips_telemetry_when_owners_is_empty()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
            .Returns(Array.Empty<LspReqnrollProject>());
        _scopeManager.GetMembershipState(Arg.Any<DocumentUri>())
            .Returns(MembershipState.Pending);

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);

        await sut.UpdateFromSourceAsync(CsUri, string.Empty, false, CancellationToken.None);

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
