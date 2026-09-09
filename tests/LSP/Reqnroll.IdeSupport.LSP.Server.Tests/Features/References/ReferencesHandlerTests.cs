using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;



using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.References;

public class StepReferencesHandlerTests
{
    private readonly IBindingMatchService          _matchService    = Substitute.For<IBindingMatchService>();
    private readonly IProjectBindingRegistryLookup _registryLookup  = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger          = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private ReferencesHandler CreateSut() => new(_matchService, _registryLookup, _logger);

    private static ReferenceParams RequestAt(DocumentUri uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position     = new Position(line, character),
            Context      = new ReferenceContext { IncludeDeclaration = false }
        };

    private static StepBindingMatch MakeMatch(DocumentUri featureUri, int startOffset, int length)
    {
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, "Feature: F\nScenario: S\n    Given a step\n");
        var range    = GherkinRange.FromPoint(snapshot, startOffset, length);
        var result   = MatchResult.NoMatch;
        return new StepBindingMatch(featureUri.ToString(), range, result);
    }

    // ── Non-.cs URI ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_cs_uri_returns_null_without_querying_match_service()
    {
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 0), CancellationToken.None);

        result.Should().BeEmpty();
        _matchService.DidNotReceive().FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    // ── No usages ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_no_binding_at_position_returns_empty_collection()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }

    // ── Single usage ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_single_usage_returns_one_location()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result!.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_location_uri_matches_feature_document_id()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result!.Single().Location!.Uri.Should().Be(FeatureUri);
    }

    // ── Multiple usages ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_multiple_usages_returns_all_locations()
    {
        var secondUri = DocumentUri.FromFileSystemPath("/workspace/other.feature");
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[]
                     {
                         MakeMatch(FeatureUri, 33, 6),
                         MakeMatch(secondUri,  33, 6)
                     });

        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        result!.Should().HaveCount(2);
    }

    // ── Position conversion ───────────────────────────────────────────────────

    [Fact]
    public async Task Handle_lsp_line_is_converted_to_one_based_source_location_line()
    {
        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestAt(CsUri, 9, 4), CancellationToken.None);

        captured!.SourceFileLine.Should().Be(10);
    }

    [Fact]
    public async Task Handle_lsp_character_is_converted_to_one_based_source_location_column()
    {
        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestAt(CsUri, 9, 4), CancellationToken.None);

        captured!.SourceFileColumn.Should().Be(5);
    }

    [Fact]
    public async Task Handle_source_location_file_path_matches_cs_uri_filesystem_path()
    {
        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestAt(CsUri, 0, 0), CancellationToken.None);

        captured!.SourceFile.Should().Be(CsUri.GetFileSystemPath());
    }

    // ── Range conversion ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_location_range_is_resolved_to_correct_line_and_character()
    {
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { MakeMatch(FeatureUri, 33, 6) });

        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 9, 0), CancellationToken.None);

        var range = result!.Single().Location!.Range;
        range.Start.Line.Should().Be(2);
        range.Start.Character.Should().Be(10);
        range.End.Line.Should().Be(2);
        range.End.Character.Should().Be(16);
    }

    // ── Project scoping (Q18 2B / issue #548) ─────────────────────────────────
    //
    // The scoping decision itself (direct owners + issue #548 widening) is now owned by
    // IProjectBindingRegistryLookup.ResolveUsageSearchScope and unit-tested against the real
    // implementation in BindingRegistryProviderRouterTests. These tests only need to prove the
    // handler forwards whatever that method returns straight through to FindUsages.

    [Fact]
    public async Task Handle_passes_the_registry_lookups_resolved_scope_to_FindUsages()
    {
        var scope = new[] { new ProjectOwner("/workspace/My.csproj", "net8.0") };
        _registryLookup.ResolveUsageSearchScope(CsUri).Returns(scope);
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestAt(CsUri, 0, 0), CancellationToken.None);

        _matchService.Received(1).FindUsages(Arg.Any<SourceLocation>(), scope);
    }

    [Fact]
    public async Task Handle_passes_null_filter_to_FindUsages_when_resolved_scope_is_null()
    {
        _registryLookup.ResolveUsageSearchScope(CsUri).Returns((IReadOnlyCollection<ProjectOwner>?)null);
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestAt(CsUri, 0, 0), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f => f == null));
    }
}
