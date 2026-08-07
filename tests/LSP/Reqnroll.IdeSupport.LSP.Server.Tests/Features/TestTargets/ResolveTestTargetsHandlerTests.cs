using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestTargets;

public class ResolveTestTargetsHandlerTests
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IScenarioTestTargetResolver _resolver = Substitute.For<IScenarioTestTargetResolver>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private const string FeatureText = "Feature: F\nScenario: S\n    Given a step\n";

    // A real, OS-valid absolute path is needed here (unlike most handler tests' "/workspace/..."
    // placeholders) because the handler builds a System.Uri from DocumentUri.GetFileSystemPath()
    // to hand to IScenarioTestTargetResolver.
    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "test.feature"));

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "Steps.cs"));

    private static readonly LspTextSnapshot Snapshot =
        new(FeatureUri.ToString(), 1, FeatureText);

    private static readonly DeveroomTag FeatureBlockTag = new(
        DeveroomTagTypes.FeatureBlock,
        new GherkinRange(Snapshot, 0, FeatureText.Length));

    private static readonly DeveroomTag ScenarioDefTag = new(
        DeveroomTagTypes.ScenarioDefinitionBlock,
        new GherkinRange(Snapshot, 11, 29)); // "Scenario: S\n    Given a step\n"

    private static readonly IReadOnlyList<DeveroomTag> AllTags =
        new[] { FeatureBlockTag, ScenarioDefTag };

    public ResolveTestTargetsHandlerTests()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        SetupBuffer(FeatureUri, FeatureText, AllTags);
    }

    private ResolveTestTargetsHandler CreateSut() =>
        new(_bufferService, _resolver, _scopeManager, _logger);

    private ResolveTestTargetsHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_bufferService, _resolver, _scopeManager, _logger, telemetry);

    private static ResolveTestTargetsParams RequestAt(DocumentUri uri, int startLine, int startChar, int endLine, int endChar) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Range = new LspRange(new Position(startLine, startChar), new Position(endLine, endChar)),
        };

    private void SetupBuffer(DocumentUri uri, string text, IReadOnlyCollection<DeveroomTag>? tags = null)
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
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_resolver_returning_empty_list_returns_empty_targets_Async()
    {
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        result.Targets.Should().BeEmpty();
    }

    // ── DTO mapping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_maps_a_non_parameterized_target_to_a_dto_Async()
    {
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new[] { new ScenarioTestTarget("Tests.FFeature", "S", false, null, null) });

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        var dto = result.Targets.Should().ContainSingle().Subject;
        dto.DeclaringTypeFullName.Should().Be("Tests.FFeature");
        dto.MethodName.Should().Be("S");
        dto.IsParameterized.Should().BeFalse();
        dto.RowArguments.Should().BeNull();
        dto.RowIndex.Should().BeNull();
    }

    [Fact]
    public async Task Handle_maps_a_parameterized_row_target_to_a_dto_Async()
    {
        var rowArgs = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new[] { new ScenarioTestTarget("Tests.FFeature", "S", true, rowArgs, 2) });

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        var dto = result.Targets.Should().ContainSingle().Subject;
        dto.IsParameterized.Should().BeTrue();
        dto.RowArguments.Should().BeEquivalentTo(rowArgs);
        dto.RowIndex.Should().Be(2);
    }

    [Fact]
    public async Task Handle_maps_multiple_targets_in_order_Async()
    {
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(new[]
            {
                new ScenarioTestTarget("Tests.FFeature", "S", true, null, 0),
                new ScenarioTestTarget("Tests.FFeature", "S", true, null, 1),
            });

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        result.Targets.Select(t => t.RowIndex).Should().BeEquivalentTo(new int?[] { 0, 1 }, o => o.WithStrictOrdering());
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
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        await CreateSut().HandleAsync(RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        _resolver.Received(1).Resolve(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(),
            Arg.Any<IReadOnlyCollection<string>>(), projectFolder);
    }

    [Fact]
    public async Task Handle_passes_a_null_project_folder_when_no_owner_resolves_Async()
    {
        _scopeManager.ResolvePrimaryOwner(FeatureUri).Returns((LspReqnrollProject?)null);
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>())
            .Returns(Array.Empty<ScenarioTestTarget>());

        await CreateSut().HandleAsync(RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        _resolver.Received(1).Resolve(
            Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(),
            Arg.Any<IReadOnlyCollection<string>>(), (string?)null);
    }

    // ── Telemetry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_emits_command_telemetry_Async()
    {
        _resolver.Resolve(Arg.Any<Uri>(), Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<GherkinRange>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(Array.Empty<ScenarioTestTarget>());
        var telemetry = Substitute.For<ILspTelemetryService>();

        await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(FeatureUri, 1, 0, 1, 11), CancellationToken.None);

        telemetry.Received(1).SendEvent("ResolveTestTargets command executed", Arg.Any<Dictionary<string, object?>>());
    }
}
