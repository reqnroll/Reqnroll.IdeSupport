using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// The source-level fallback of <see cref="ConnectorBindingRegistryProvider"/>: when a connector
/// run ends without bindings (failed, or no output assembly yet), the project's binding files from
/// the membership index are parsed from source so the registry is not left empty until each file
/// is opened.
/// </summary>
public class ConnectorSourceFallbackTests : IDisposable
{
    private const string StepsSource = """
        using Reqnroll;
        namespace Sample;
        [Binding]
        public class Steps
        {
            [Given("the first step")] public void First() { }
        }
        """;

    private const string MoreStepsSource = """
        using Reqnroll;
        namespace Sample;
        [Binding]
        public class MoreSteps
        {
            [When("the second step")] public void Second() { }
            [BeforeScenario] public void Setup() { }
        }
        """;

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IConnectorDiscoveryService _discovery = Substitute.For<IConnectorDiscoveryService>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly ICSharpFileTextCache _csharpFileTextCache = new CSharpFileTextCache();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CSFTests_" + Guid.NewGuid().ToString("N"));
    private readonly LspReqnrollProject _project;
    private readonly List<string> _indexedBindingFiles = [];

    public ConnectorSourceFallbackTests()
    {
        Directory.CreateDirectory(_folder);
        _project = DiscoveryTestSupport.MakeProject(new LspIdeScope(_logger), _folder);

        // The membership index has a baseline listing the project's binding files.
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(_project).Returns(_ => _indexedBindingFiles.ToArray());
    }

    public void Dispose()
    {
        _project.Dispose();
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string GivenIndexedBindingFile(string name, string source)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, source);
        _indexedBindingFiles.Add(path);
        return path;
    }

    private void GivenDiscoveryReturns(ConnectorDiscoveryStatus status)
        => _discovery.RunDiscovery(
                Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new ConnectorDiscoveryOutcome(
                status, call.ArgAt<ProjectBindingRegistry>(1), call.ArgAt<string>(2)));

    private ConnectorBindingRegistryProvider CreateSut() => new(
        _project, _discovery, _logger, telemetryService: null,
        sourceFileProvider: new CsFileReconciliationSelector(
            _scopeManager, _csharpFileTextCache, new FileSystemForIDE(), _logger));

    private static async Task<bool> RefreshAndWaitForFullReplacementAsync(ConnectorBindingRegistryProvider sut)
    {
        var replaced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.BindingRegistryChanged += (_, isFullReplacement) => replaced.TrySetResult(isFullReplacement);
        sut.TriggerRefresh();
        var completed = await Task.WhenAny(replaced.Task, Task.Delay(5000));
        return completed == replaced.Task && await replaced.Task;
    }

    private async Task RefreshAndWaitForRunAsync(ConnectorBindingRegistryProvider sut)
    {
        var ran = new TaskCompletionSource();
        _discovery.When(d => d.RunDiscovery(
                Arg.Any<IProjectScope>(), Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => ran.TrySetResult());
        sut.TriggerRefresh();
        await Task.WhenAny(ran.Task, Task.Delay(5000));
        await Task.Delay(300);
    }

    [Fact]
    public async Task Failed_connector_run_yields_bindings_from_every_indexed_binding_file()
    {
        GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenIndexedBindingFile("MoreSteps.cs", MoreStepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Failed);
        using var sut = CreateSut();

        (await RefreshAndWaitForFullReplacementAsync(sut))
            .Should().BeTrue("a fallback that added bindings is announced as a full replacement");

        sut.Current.StepDefinitions.Select(b => b.Expression)
            .Should().BeEquivalentTo("the first step", "the second step");
        sut.Current.Hooks.Should().HaveCount(1);
        sut.HasSuccessfulConnectorRun.Should().BeFalse("the bindings came from source, not the connector");
    }

    [Fact]
    public async Task Skipped_connector_run_on_an_unbuilt_project_yields_bindings_from_source()
    {
        GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Skipped);
        using var sut = CreateSut();

        (await RefreshAndWaitForFullReplacementAsync(sut)).Should().BeTrue();

        sut.Current.StepDefinitions.Select(b => b.Expression).Should().BeEquivalentTo("the first step");
    }

    [Fact]
    public async Task Open_buffer_text_wins_over_the_file_on_disk()
    {
        var path = GivenIndexedBindingFile("Steps.cs", StepsSource);
        var uri = DocumentUri.FromFileSystemPath(path);
        _csharpFileTextCache.Update(uri, StepsSource.Replace("the first step", "the edited step"));
        _scopeManager.ResolveOwners(uri).Returns(new[] { _project });
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Failed);
        using var sut = CreateSut();

        (await RefreshAndWaitForFullReplacementAsync(sut)).Should().BeTrue();

        sut.Current.StepDefinitions.Select(b => b.Expression).Should().BeEquivalentTo("the edited step");
    }

    [Fact]
    public async Task Fallback_fills_only_files_the_registry_has_no_bindings_for()
    {
        var steps = GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Failed);
        using var sut = CreateSut();
        (await RefreshAndWaitForFullReplacementAsync(sut)).Should().BeTrue();

        // A later failed run: the covered file changed on disk, and a new file appeared. The
        // covered file keeps the bindings it already has; only the new file is filled in.
        File.WriteAllText(steps, StepsSource.Replace("the first step", "changed on disk"));
        GivenIndexedBindingFile("MoreSteps.cs", MoreStepsSource);
        (await RefreshAndWaitForFullReplacementAsync(sut)).Should().BeTrue();

        sut.Current.StepDefinitions.Select(b => b.Expression)
            .Should().BeEquivalentTo("the first step", "the second step");
    }

    [Fact]
    public async Task Fallback_does_not_notify_when_every_file_is_already_covered()
    {
        GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Failed);
        using var sut = CreateSut();
        (await RefreshAndWaitForFullReplacementAsync(sut)).Should().BeTrue();
        var afterFirst = sut.Current;

        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;
        await RefreshAndWaitForRunAsync(sut);

        raised.Should().BeFalse();
        sut.Current.Should().BeSameAs(afterFirst);
    }

    [Fact]
    public async Task No_fallback_for_a_project_that_is_not_a_Reqnroll_test_project()
    {
        GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.NotReqnrollProject);
        using var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        await RefreshAndWaitForRunAsync(sut);

        raised.Should().BeFalse();
        sut.Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }

    [Fact]
    public async Task No_fallback_after_an_unchanged_assembly()
    {
        GivenIndexedBindingFile("Steps.cs", StepsSource);
        GivenDiscoveryReturns(ConnectorDiscoveryStatus.Unchanged);
        using var sut = CreateSut();
        var raised = false;
        sut.BindingRegistryChanged += (_, _) => raised = true;

        await RefreshAndWaitForRunAsync(sut);

        raised.Should().BeFalse();
        sut.Current.Should().BeSameAs(ProjectBindingRegistry.Invalid);
    }
}
