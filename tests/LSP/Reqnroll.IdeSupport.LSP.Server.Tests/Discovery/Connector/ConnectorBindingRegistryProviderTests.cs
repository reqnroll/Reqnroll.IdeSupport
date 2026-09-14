using System.Collections.Immutable;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

public class ConnectorBindingRegistryProviderTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IConnectorDiscoveryService _discovery = Substitute.For<IConnectorDiscoveryService>();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly LspReqnrollProject _project;

    public ConnectorBindingRegistryProviderTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _project = DiscoveryTestSupport.MakeProject(_ideScope, _folder);
    }

    public void Dispose() => _project.Dispose();

    private ConnectorBindingRegistryProvider CreateSut() => new(_project, _discovery, _logger);

    private ConnectorBindingRegistryProvider CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_project, _discovery, _logger, telemetry);

    private static ProjectBindingRegistry NonInvalidRegistry(int hash) => new(
        ImmutableArray<ProjectStepDefinitionBinding>.Empty,
        ImmutableArray<ProjectHookBinding>.Empty,
        hash);

    private void GivenDiscoveryReturns(ProjectBindingRegistry registry, string hash)
        => _discovery.RunDiscovery(
                Arg.Any<IProjectScope>(),
                Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((registry, hash));

    // ── Initial state ──────────────────────────────────────────────────────────

    [Fact]
    public void Current_is_invalid_before_any_discovery_runs()
    {
        CreateSut().Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    [Fact]
    public void HasSuccessfulConnectorRun_is_false_before_any_discovery_runs()
    {
        CreateSut().HasSuccessfulConnectorRun.Should().BeFalse();
    }

    // ── Successful refresh ──────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerRefresh_swaps_registry_and_raises_event_on_new_result()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();

        var completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
        completed.Should().BeSameAs(changed.Task, "discovery should complete and raise the change event");
        sut.Current.Should().BeSameAs(newRegistry);
    }

    // Issue #471: CSharpBindingDiscoveryService.UpdateFromSourceAsync uses this flag to skip a
    // redundant source-level parse on textDocument/didOpen once the connector has already covered
    // the project.
    [Fact]
    public async Task TriggerRefresh_sets_HasSuccessfulConnectorRun_true_on_a_real_swap()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();
        await Task.WhenAny(changed.Task, Task.Delay(5000));

        sut.HasSuccessfulConnectorRun.Should().BeTrue();
    }

    // ── No-op refresh ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerRefresh_does_not_raise_event_when_hash_is_unchanged()
    {
        // Discovery returns the last-good registry with the same (empty) hash → no swap.
        GivenDiscoveryReturns(ProjectBindingRegistry.Invalid, string.Empty);
        var telemetry = Substitute.For<ILspTelemetryService>();

        // The hash-noop branch always ends in a telemetry send (see
        // TriggerRefresh_emits_hash_noop_telemetry_when_hash_unchanged below) — the only
        // synchronous signal available to wait on for "the debounced run has fully settled"
        // in this no-swap path, since BindingRegistryChanged never fires here by design.
        var settled = new TaskCompletionSource();
        telemetry.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => settled.TrySetResult());

        var sut = CreateSutWithTelemetry(telemetry);
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        sut.TriggerRefresh();
        await Task.WhenAny(settled.Task, Task.Delay(5000));

        raised.Should().BeFalse();
        sut.Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
        _discovery.ReceivedWithAnyArgs().RunDiscovery(default!, default!, default!, default);
        // Issue #471: the hash-match no-op path is exactly the "no compiled DLL yet" case (see
        // ConnectorDiscoveryService.RunDiscovery, which returns the unchanged lastHash whenever
        // OutputAssemblyPath is unset or the file doesn't exist) -- HasSuccessfulConnectorRun must
        // stay false here so CSharpBindingDiscoveryService keeps relying on didOpen/didChange as
        // the only source of bindings for an unbuilt project.
        sut.HasSuccessfulConnectorRun.Should().BeFalse();
    }

    // ── Debounce: rapid triggers collapse to a single run ────────────────────────

    [Fact]
    public async Task TriggerRefresh_called_rapidly_cancels_earlier_runs()
    {
        var newRegistry = NonInvalidRegistry(hash: 7);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        // Three triggers inside the debounce window: only the last should survive to run.
        sut.TriggerRefresh();
        sut.TriggerRefresh();
        sut.TriggerRefresh();

        await Task.WhenAny(changed.Task, Task.Delay(5000));
        sut.Current.Should().BeSameAs(newRegistry);

        // The cancelled earlier runs never reach the discovery service; only one run executes.
        _discovery.ReceivedWithAnyArgs(1).RunDiscovery(default!, default!, default!, default);
    }

    // ── Roslyn source-level patch (F2) ───────────────────────────────────────────

    [Fact]
    public async Task ApplyRoslynFileUpdate_patches_current_registry_and_raises_event()
    {
        var sut = CreateSut();
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        var file = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { }
    }
}");

        await sut.ApplyRoslynFileUpdateAsync(file);

        (await Task.WhenAny(changed.Task, Task.Delay(2000)))
            .Should().BeSameAs(changed.Task, "the source-level update should raise BindingRegistryChanged");
        sut.Current.Should().NotBeSameAs(ProjectBindingRegistry.Invalid);
        sut.Current.StepDefinitions.Should().ContainSingle()
            .Which.Regex!.ToString().Should().Be("^the first number is (.*)$");
    }

    // Issue #471: notify: false is used by BindingRegistryChangedHandler.RediscoverCsFilesAsync,
    // whose own caller already reparses every open feature file and notifies unconditionally right
    // after it returns -- a second independent event here would just redundantly repeat that work.
    [Fact]
    public async Task ApplyRoslynFileUpdate_still_patches_current_registry_but_does_not_raise_event_when_notify_is_false()
    {
        var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        var file = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { }
    }
}");

        await sut.ApplyRoslynFileUpdateAsync(file, notify: false);
        await Task.Delay(200);

        raised.Should().BeFalse("notify: false must suppress BindingRegistryChanged even though the patch changed something");
        sut.Current.Should().NotBeSameAs(ProjectBindingRegistry.Invalid, "the registry must still be patched regardless of notify");
        sut.Current.StepDefinitions.Should().ContainSingle()
            .Which.Regex!.ToString().Should().Be("^the first number is (.*)$");
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_raises_event_as_incremental_not_full_replacement()
    {
        var sut = CreateSut();
        bool? isFullReplacement = null;
        sut.BindingRegistryChanged += (_, full) => isFullReplacement = full;

        var file = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(file);

        isFullReplacement.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_does_not_raise_event_when_only_a_method_body_changes()
    {
        var sut = CreateSut();

        var original = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { var unused = 1; }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(original);

        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        var bodyEdited = FileDetailsFor("Steps.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Steps
    {
        [Reqnroll.Given(""the first number is (.*)"")]
        public void Method(int n) { var unused = 2; }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(bodyEdited);

        raised.Should().BeFalse("only the method body changed, not the binding's matched expression -- there's nothing for feature-file matching to recompute");
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_raises_event_when_a_hook_is_added_with_no_step_definition_change()
    {
        // Regression test for the live-reported bug: adding a hook attribute to a .cs file
        // didn't refresh the feature file's hook-count CodeLens until a full rebuild, because
        // the notification gate only checked ProjectBindingRegistry.HasExpressionChanges
        // (step definitions), never ProjectBindingRegistry.HasHookChanges.
        var sut = CreateSut();

        var original = FileDetailsFor("Hooks.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Hooks
    {
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(original);

        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        var hookAdded = FileDetailsFor("Hooks.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Hooks
    {
        [Reqnroll.BeforeScenario]
        public void SetUp() { }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(hookAdded);

        raised.Should().BeTrue("a hook was added even though no step definition changed");
        sut.Current.Hooks.Should().ContainSingle().Which.HookType.Should().Be(HookType.BeforeScenario);
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_does_not_raise_event_when_a_hooks_method_body_changes()
    {
        var sut = CreateSut();

        var original = FileDetailsFor("Hooks.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Hooks
    {
        [Reqnroll.BeforeScenario]
        public void SetUp() { var unused = 1; }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(original);

        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        var bodyEdited = FileDetailsFor("Hooks.cs", @"
namespace S
{
    [Reqnroll.Binding]
    public class Hooks
    {
        [Reqnroll.BeforeScenario]
        public void SetUp() { var unused = 2; }
    }
}");
        await sut.ApplyRoslynFileUpdateAsync(bodyEdited);

        raised.Should().BeFalse("only the method body changed, not the hook's scope/order -- there's nothing for feature-file matching to recompute");
    }

    [Fact]
    public async Task ApplyRoslynFileUpdate_replaces_only_that_files_bindings()
    {
        var sut = CreateSut();

        var first = FileDetailsFor("A.cs",
            "namespace S { [Reqnroll.Binding] class A { [Reqnroll.Given(\"a\")] void M(){} } }");
        var second = FileDetailsFor("B.cs",
            "namespace S { [Reqnroll.Binding] class B { [Reqnroll.Given(\"b\")] void M(){} } }");

        await sut.ApplyRoslynFileUpdateAsync(first);
        await sut.ApplyRoslynFileUpdateAsync(second);

        // Editing B.cs again must keep A.cs's binding and replace only B.cs's.
        var secondEdited = FileDetailsFor("B.cs",
            "namespace S { [Reqnroll.Binding] class B { [Reqnroll.Given(\"b2\")] void M(){} } }");
        await sut.ApplyRoslynFileUpdateAsync(secondEdited);

        sut.Current.StepDefinitions.Select(s => s.Expression)
            .Should().BeEquivalentTo(new[] { "a", "b2" });
    }

    private CSharpStepDefinitionFile FileDetailsFor(string fileName, string content) =>
        Reqnroll.IdeSupport.Common.FileDetails
            .FromPath(Path.Combine(_folder, fileName))
            .WithCSharpContent(content);

    // ── Dispose ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_safe_to_call_without_any_refresh()
    {
        var sut = CreateSut();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_refresh_so_no_event_is_raised()
    {
        GivenDiscoveryReturns(NonInvalidRegistry(1), "hash-1");

        var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        sut.TriggerRefresh();      // schedules a run after the 500 ms debounce
        sut.Dispose();             // cancels it before the debounce elapses
        await Task.Delay(1000);

        raised.Should().BeFalse();
    }

    // ── Telemetry ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerRefresh_emits_discovery_telemetry_on_new_result()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");
        var telemetry = Substitute.For<ILspTelemetryService>();

        var sut = CreateSutWithTelemetry(telemetry);
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();
        await Task.WhenAny(changed.Task, Task.Delay(5000));

        telemetry.Received(1).SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d =>
                "Connector".Equals(d["DiscoverySource"]) &&
                "projectLoad".Equals(d["TriggerContext"]) &&
                false.Equals(d["IsFailed"]) &&
                0.Equals(d["StepDefinitionCount"]) &&
                0.Equals(d["HookCount"])));
    }

    [Fact]
    public async Task TriggerRefresh_emits_hash_noop_telemetry_when_hash_unchanged()
    {
        GivenDiscoveryReturns(ProjectBindingRegistry.Invalid, string.Empty);
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sent = new TaskCompletionSource();
        telemetry.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => sent.TrySetResult());

        var sut = CreateSutWithTelemetry(telemetry);
        sut.TriggerRefresh();
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        telemetry.Received(1).SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d =>
                "Connector".Equals(d["DiscoverySource"]) &&
                true.Equals(d["HashMatched"])));
    }

    [Fact]
    public async Task TriggerRefresh_sets_triggerContext_to_build_on_second_run()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");
        var telemetry = Substitute.For<ILspTelemetryService>();

        var sut = CreateSutWithTelemetry(telemetry);

        // First run — should be projectLoad
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();
        sut.TriggerRefresh();
        await Task.WhenAny(changed.Task, Task.Delay(5000));

        // Second run with different hash — should be build
        var newRegistry2 = NonInvalidRegistry(hash: 43);
        GivenDiscoveryReturns(newRegistry2, "hash-2");
        changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();
        sut.TriggerRefresh();
        await Task.WhenAny(changed.Task, Task.Delay(5000));

        telemetry.Received(1).SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d => "projectLoad".Equals(d["TriggerContext"])));
        telemetry.Received(1).SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d => "build".Equals(d["TriggerContext"])));
    }

    [Fact]
    public async Task TriggerRefresh_emits_failure_telemetry_when_discovery_throws()
    {
        _discovery.RunDiscovery(
                Arg.Any<IProjectScope>(),
                Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("connector boom"));
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sent = new TaskCompletionSource();
        telemetry.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(_ => sent.TrySetResult());

        var sut = CreateSutWithTelemetry(telemetry);
        sut.TriggerRefresh();
        await Task.WhenAny(sent.Task, Task.Delay(5000));

        telemetry.Received(1).SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d =>
                "Connector".Equals(d["DiscoverySource"]) &&
                "projectLoad".Equals(d["TriggerContext"]) &&
                true.Equals(d["IsFailed"]) &&
                "connector boom".Equals(d["ErrorMessage"])));
    }

    [Fact]
    public async Task TriggerRefresh_does_not_emit_failure_telemetry_on_cancellation()
    {
        // A run cancelled by a newer trigger is normal, not a failure — no telemetry.
        var newRegistry = NonInvalidRegistry(hash: 7);
        GivenDiscoveryReturns(newRegistry, "hash-1");
        var telemetry = Substitute.For<ILspTelemetryService>();

        var sut = CreateSutWithTelemetry(telemetry);
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();
        sut.TriggerRefresh(); // cancels the first in-flight run
        await Task.WhenAny(changed.Task, Task.Delay(5000));

        telemetry.DidNotReceive().SendEvent(
            TelemetryEvents.ReqnrollDiscoveryExecuted,
            Arg.Is<Dictionary<string, object?>>(d => true.Equals(d["IsFailed"])));
    }

    [Fact]
    public async Task TriggerRefresh_does_not_throw_when_telemetry_service_is_null()
    {
        var newRegistry = NonInvalidRegistry(hash: 42);
        GivenDiscoveryReturns(newRegistry, "hash-1");

        var sut = CreateSut(); // no telemetry
        var changed = new TaskCompletionSource();
        sut.BindingRegistryChanged += (_, _) => changed.TrySetResult();

        sut.TriggerRefresh();
        var completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
        completed.Should().BeSameAs(changed.Task, "discovery should still complete without telemetry");
    }
}
