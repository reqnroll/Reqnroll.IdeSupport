using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestTargets;

public class ResolveContainerTestTargetsHandlerTests
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IScenarioTestTargetResolver _resolver = Substitute.For<IScenarioTestTargetResolver>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private const string FeatureText = "Feature: F\nScenario: S1\n    Given a step\nScenario: S2\n    Given a step\n";

    // A real, OS-valid absolute path is needed here (mirrors ResolveTestTargetsHandlerTests) because
    // the handler builds a System.Uri from DocumentUri.GetFileSystemPath() to hand to the resolver.
    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "container-test.feature"));

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "Steps.cs"));

    private static readonly LspTextSnapshot Snapshot =
        new(FeatureUri.ToString(), 1, FeatureText);

    private static readonly IdeSupportTag FeatureBlockTag = new(
        IdeSupportTagTypes.FeatureBlock,
        new GherkinRange(Snapshot, 0, FeatureText.Length));

    private static readonly IReadOnlyList<IdeSupportTag> AllTags = new[] { FeatureBlockTag };

    public ResolveContainerTestTargetsHandlerTests()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        SetupBuffer(FeatureUri, FeatureText, AllTags);
    }

    private ResolveContainerTestTargetsHandler CreateSut() =>
        new(_bufferService, _resolver, _scopeManager, _logger);

    private ResolveContainerTestTargetsHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_bufferService, _resolver, _scopeManager, _logger, telemetry);

    private static ResolveContainerTestTargetsParams RequestAt(DocumentUri uri, int startLine, int startChar, int endLine, int endChar) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Range = new LspRange(new Position(startLine, startChar), new Position(endLine, endChar)),
        };

    private void SetupBuffer(DocumentUri uri, string text, IReadOnlyCollection<IdeSupportTag>? tags = null)
    {
        var buf = new DocumentBuffer(uri, 1, text, tags);
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_feature_uri_returns_empty_targets_Async()
    {
        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 0, 0, 0, 0), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_missing_buffer_returns_empty_targets_Async()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/untracked.feature");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(
            RequestAt(uri, 0, 0, 0, 0), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_buffer_with_null_tags_returns_empty_targets_Async()
    {
        SetupBuffer(FeatureUri, FeatureText, tags: null);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_resolver_returning_empty_list_returns_empty_targets_Async()
    {
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    // ── DTO mapping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_maps_multiple_scenarios_targets_to_dtos_Async()
    {
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>())
            .Returns(new[]
            {
                new ScenarioTestTarget("Tests.FFeature", "S1", false, null, null),
                new ScenarioTestTarget("Tests.FFeature", "S2", false, null, null),
            });

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        result.Targets.Select(t => t.MethodName).Should().BeEquivalentTo(
            new[] { "S1", "S2" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Handle_maps_a_parameterized_row_target_to_a_dto_Async()
    {
        var rowArgs = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>())
            .Returns(new[] { new ScenarioTestTarget("Tests.FFeature", "S1", true, rowArgs, 2) });

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        var dto = result.Targets.Should().ContainSingle().Subject;
        dto.IsParameterized.Should().BeTrue();
        dto.RowArguments.Should().BeEquivalentTo(rowArgs);
        dto.RowIndex.Should().Be(2);
    }

    // ── Project folder threading (Reqnroll 3.3.0 obj-relocated code-behind) ─────

    [Fact]
    public async Task Handle_passes_the_primary_owners_project_folder_to_the_resolver_Async()
    {
        var projectFolder = Path.Combine(Path.GetTempPath(), "SomeProject");
        var project = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder = projectFolder,
                ProjectFile = Path.Combine(projectFolder, "SomeProject.csproj"),
                ProjectFolder = projectFolder,
                OutputAssemblyPath = Path.Combine(projectFolder, "bin", "SomeProject.dll"),
                TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0",
            },
            Substitute.For<IIdeScope>());
        _scopeManager.ResolvePrimaryOwner(FeatureUri).Returns(project);
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>(), Arg.Any<string?>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        await CreateSut().HandleAsync(RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        _resolver.Received(1).ResolveAll(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>(),
            projectFolder);
    }

    [Fact]
    public async Task Handle_passes_a_null_project_folder_when_no_owner_resolves_Async()
    {
        _scopeManager.ResolvePrimaryOwner(FeatureUri).Returns((LspReqnrollProject?)null);
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>(), Arg.Any<string?>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        await CreateSut().HandleAsync(RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        _resolver.Received(1).ResolveAll(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>(),
            (string?)null);
    }

    // ── Telemetry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_emits_command_telemetry_Async()
    {
        _resolver.ResolveAll(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>(), Arg.Any<GherkinRange>())
            .Returns(Array.Empty<ScenarioTestTarget>());
        var telemetry = Substitute.For<ILspTelemetryService>();

        await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(FeatureUri, 0, 0, 4, 0), CancellationToken.None);

        telemetry.Received(1).SendEvent(
            "ResolveContainerTestTargets command executed",
            Arg.Is<Dictionary<string, object?>>(p => (int)p["TargetCount"]! == 0 && (string)p["Kind"]! == "Feature"));
    }
}
