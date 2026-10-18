using System.Collections.Concurrent;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Issue #939: the real <see cref="ConnectorDiscoveryService"/> driving the real
/// <see cref="ConnectorBindingRegistryProvider"/>, so the provider sees exactly what each
/// non-success path of the service returns. A run that failed or never reached the connector must
/// not be reported as an unchanged-hash success, and must not use up the "projectLoad" trigger
/// context that the first real discovery run should carry.
/// </summary>
public class ConnectorDiscoveryOutcomeTelemetryTests : IDisposable
{
    // Comfortably past the provider's 500 ms debounce; used where a run, by design, raises no
    // event that could be waited on instead.
    private const int SettleMilliseconds = 1500;

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IOutProcConnectorFactory _factory = Substitute.For<IOutProcConnectorFactory>();
    private readonly IReqnrollProjectDetector _detector = Substitute.For<IReqnrollProjectDetector>();
    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();
    private readonly ConcurrentQueue<Dictionary<string, object?>> _discoveryEvents = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _assemblyPath;
    private readonly LspReqnrollProject _project;
    private readonly ConnectorBindingRegistryProvider _sut;

    public ConnectorDiscoveryOutcomeTelemetryTests()
    {
        Directory.CreateDirectory(_folder);
        _assemblyPath = Path.Combine(_folder, "MyApp.Tests.dll");
        _project = DiscoveryTestSupport.MakeProject(new LspIdeScope(_logger), _folder,
            outputAssemblyPath: _assemblyPath);

        _detector.IsReqnrollTestProject(Arg.Any<IProjectScope>()).Returns(true);
        _telemetry.When(t => t.SendEvent(TelemetryEvents.ReqnrollDiscoveryExecuted,
                Arg.Any<Dictionary<string, object?>>()))
            .Do(call => _discoveryEvents.Enqueue(call.ArgAt<Dictionary<string, object?>>(1)));

        var service = new ConnectorDiscoveryService(_logger, _factory, new FileSystemForIDE(), _detector);
        _sut = new ConnectorBindingRegistryProvider(_project, service, _logger, _telemetry);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _project.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    public enum NonSuccess
    {
        ConnectorReportsFailure,
        ConnectorThrows,
        AssemblyMissing,
        NotReqnrollProject,
    }

    // ── Arrangement ─────────────────────────────────────────────────────────────

    private void Given(NonSuccess kind)
    {
        switch (kind)
        {
            case NonSuccess.ConnectorReportsFailure:
                File.WriteAllText(_assemblyPath, "not a real assembly");
                _factory.Create(Arg.Any<IProjectScope>())
                    .Returns(new FakeConnector(new DiscoveryResult { ErrorMessage = "boom" }));
                break;
            case NonSuccess.ConnectorThrows:
                File.WriteAllText(_assemblyPath, "not a real assembly");
                _factory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(null));
                break;
            case NonSuccess.AssemblyMissing:
                // No file written at _assemblyPath: the project has not been built yet.
                break;
            case NonSuccess.NotReqnrollProject:
                File.WriteAllText(_assemblyPath, "not a real assembly");
                _detector.IsReqnrollTestProject(Arg.Any<IProjectScope>()).Returns(false);
                break;
        }
    }

    /// <summary>Turns whatever <see cref="Given"/> set up into a project whose next run succeeds.</summary>
    private void ThenTheProjectBecomesDiscoverable()
    {
        // A fresh write gives the assembly a new last-write time, i.e. a new content hash.
        File.WriteAllText(_assemblyPath, "rebuilt");
        File.SetLastWriteTimeUtc(_assemblyPath, DateTime.UtcNow.AddMinutes(1));
        _detector.IsReqnrollTestProject(Arg.Any<IProjectScope>()).Returns(true);
        _factory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(SuccessfulResult()));
    }

    private static DiscoveryResult SuccessfulResult() => new()
    {
        StepDefinitions =
        [
            new StepDefinition
            {
                Type           = "Given",
                Regex          = "^the first number is (.*)$",
                Method         = "MyApp.Steps.SetFirstNumber",
                ParamTypes     = "i",
                SourceLocation = "Steps.cs|10|5"
            }
        ],
        Hooks = []
    };

    private async Task TriggerAndSettleAsync()
    {
        _sut.TriggerRefresh();
        await Task.Delay(SettleMilliseconds);
    }

    private async Task TriggerAndWaitForSwapAsync()
    {
        var changed = new TaskCompletionSource();
        void OnChanged(object? sender, bool full) => changed.TrySetResult();
        _sut.BindingRegistryChanged += OnChanged;
        try
        {
            _sut.TriggerRefresh();
            var completed = await Task.WhenAny(changed.Task, Task.Delay(5000));
            completed.Should().BeSameAs(changed.Task, "test setup: the successful run must swap the registry");
        }
        finally
        {
            _sut.BindingRegistryChanged -= OnChanged;
        }
    }

    private static bool IsHashMatchSuccess(Dictionary<string, object?> e) =>
        e.TryGetValue("HashMatched", out var matched) && true.Equals(matched) &&
        e.TryGetValue("IsFailed", out var failed) && false.Equals(failed);

    // ── Failed / skipped runs are not unchanged-hash successes ───────────────────

    [Theory]
    [InlineData(NonSuccess.ConnectorReportsFailure)]
    [InlineData(NonSuccess.ConnectorThrows)]
    [InlineData(NonSuccess.AssemblyMissing)]
    [InlineData(NonSuccess.NotReqnrollProject)]
    public async Task A_run_that_did_not_discover_is_not_reported_as_a_hash_match_success(NonSuccess kind)
    {
        Given(kind);

        await TriggerAndSettleAsync();

        _discoveryEvents.Should().NotContain(e => IsHashMatchSuccess(e),
            $"a {kind} run never produced bindings, so it is not an unchanged-hash no-op");
    }

    [Theory]
    [InlineData(NonSuccess.ConnectorReportsFailure)]
    [InlineData(NonSuccess.ConnectorThrows)]
    public async Task A_failed_connector_run_is_reported_as_a_failure(NonSuccess kind)
    {
        Given(kind);

        await TriggerAndSettleAsync();

        _discoveryEvents.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, object?>
        {
            ["DiscoverySource"] = "Connector",
            ["TriggerContext"] = "projectLoad",
            ["IsFailed"] = true,
        });
        _sut.HasSuccessfulConnectorRun.Should().BeFalse();
    }

    [Theory]
    [InlineData(NonSuccess.ConnectorReportsFailure)]
    [InlineData(NonSuccess.ConnectorThrows)]
    [InlineData(NonSuccess.AssemblyMissing)]
    [InlineData(NonSuccess.NotReqnrollProject)]
    public async Task The_first_successful_run_after_one_that_did_not_discover_is_still_projectLoad(NonSuccess kind)
    {
        Given(kind);
        await TriggerAndSettleAsync();

        ThenTheProjectBecomesDiscoverable();
        await TriggerAndWaitForSwapAsync();

        var success = _discoveryEvents.Should()
            .ContainSingle(e => e.ContainsKey("StepDefinitionCount"), "exactly one run swapped in bindings").Subject;
        success["TriggerContext"].Should().Be("projectLoad",
            $"the earlier {kind} run never discovered anything, so it must not use up the first-run state");
    }

    // ── The genuine unchanged-hash path is unaffected ────────────────────────────

    [Fact]
    public async Task A_rerun_against_an_unchanged_assembly_is_still_a_hash_match_success()
    {
        ThenTheProjectBecomesDiscoverable();
        await TriggerAndWaitForSwapAsync();

        await TriggerAndSettleAsync();

        _discoveryEvents.Should().HaveCount(2);
        var noop = _discoveryEvents.Last();
        IsHashMatchSuccess(noop).Should().BeTrue("the assembly did not change between the two runs");
        noop["TriggerContext"].Should().Be("build");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────

    /// <summary>Returns <paramref name="result"/>, or throws when it is null.</summary>
    private sealed class FakeConnector(DiscoveryResult? result) : OutProcReqnrollConnector(
        new IdeSupportConfiguration(),
        Substitute.For<IIdeSupportLogger>(),
        TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
        AppContext.BaseDirectory,
        ProcessorArchitectureSetting.UseSystem,
        DiscoveryTestSupport.MinimalProjectSettings(TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
        NullLspTelemetryService.Instance)
    {
        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath)
            => result ?? throw new InvalidOperationException("connector blew up");

        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }
}
