#nullable enable

using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;



using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Definition;

public class DefinitionHandlerTests
{
    // Use the real BindingMatchService to avoid NSubstitute out-param complexity.
    private BindingMatchService            _matchService  = new();
    private readonly IDocumentBufferService    _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger           _logger        = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE         _fileSystem    = new FileSystemForIDE();

    // "Feature: F\nScenario: S\n    Given a step\n"
    // Line 0: "Feature: F"         offsets  0–9  (\n at 10)
    // Line 1: "Scenario: S"        offsets 11–21 (\n at 22)
    // Line 2: "    Given a step"   offsets 23–38 (\n at 39)
    //   step text "a step" starts at offset 33 (after "    Given "), length 6
    //   LSP position for offset 33: line=2, character=10
    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n";

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    public DefinitionHandlerTests()
    {
        // Default: no primary owner
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
                     .Returns((LspReqnrollProject?)null);

        SetupBuffer(FeatureUri, FeatureText);
    }

    private DefinitionHandler CreateSut() =>
        new(_matchService, _bufferService, _scopeManager, _logger, _fileSystem);

    private static DefinitionParams RequestAt(DocumentUri uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position     = new Position(line, character)
        };

    private void SetupBuffer(DocumentUri uri, string text)
    {
        var buf = new DocumentBuffer(uri, 1, text);
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    /// <summary>
    /// Builds a defined StepBindingMatch with the step at offset 33 (length 6) in FeatureText,
    /// pointing to <paramref name="csFile"/>:<paramref name="csLine"/>:<paramref name="csColumn"/>.
    /// </summary>
    private static StepBindingMatch MakeDefinedMatch(
        string csFile, int csLine, int csColumn,
        int startOffset = 33, int length = 6,
        int? csEndLine = null, int? csEndColumn = null)
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, startOffset, length);

        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex("^a step$"),
            null,
            new ProjectBindingImplementation(
                "AStep",
                null,
                new SourceLocation(csFile, csLine, csColumn, csEndLine, csEndColumn)));

        var item   = MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);
        var result = MatchResult.CreateMultiMatch(new[] { item });

        return new StepBindingMatch(FeatureUri.ToString(), range, result);
    }

    // ── Non-.feature URI ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_feature_uri_returns_empty_Async()
    {
        var result = await CreateSut().Handle(
            RequestAt(CsUri, 0, 0), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── No buffer ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_missing_buffer_returns_empty_Async()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/untracked.feature");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(false);

        var result = await CreateSut().Handle(
            RequestAt(uri, 2, 10), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── No match set in cache ─────────────────────────────────────────────────

    [Fact]
    public async Task Handle_no_match_set_cached_returns_empty_Async()
    {
        // Nothing stored in _matchService → TryGet returns false
        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── No step at cursor ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_cursor_not_on_step_returns_empty_Async()
    {
        // Store an empty match set (no steps)
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, Array.Empty<StepBindingMatch>()));

        // Cursor on the Feature: line — no step there
        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 0, 0), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Defined step — single binding ─────────────────────────────────────────

    [Fact]
    public async Task Handle_defined_step_returns_one_location_Async()
    {
        var step = MakeDefinedMatch("Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_location_uri_points_to_cs_file_Async()
    {
        var step = MakeDefinedMatch("/workspace/Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result!.Single().Location!.Uri.GetFileSystemPath()
            .Should().Be("/workspace/Steps.cs");
    }

    [Fact]
    public async Task Handle_location_range_start_is_converted_from_one_based_to_zero_based_Async()
    {
        // SourceLocation: line 10 (1-based), column 5 (1-based)
        // Expected LSP:   line 9  (0-based), character 4 (0-based)
        var step = MakeDefinedMatch("Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var range = result!.Single().Location!.Range;
        range.Start.Line.Should().Be(9);
        range.Start.Character.Should().Be(4);
    }

    [Fact]
    public async Task Handle_location_range_is_always_zero_width_Async()
    {
        // End-position data from the discovery layer (e.g. PDB body span from the Connector)
        // must be discarded — the LSP definition range is always zero-width at the start.
        var step = MakeDefinedMatch(
            "Steps.cs", csLine: 10, csColumn: 5,
            csEndLine: 10, csEndColumn: 15);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var range = result!.Single().Location!.Range;
        range.End.Line.Should().Be(range.Start.Line);
        range.End.Character.Should().Be(range.Start.Character);
    }

    // ── Cursor beyond the exact step text span but still on the step's line (#101) ────────────

    [Fact]
    public async Task Handle_cursor_at_end_of_step_line_resolves_Async()
    {
        // Line 2 is "    Given a step" (17 chars) — character 16 is one past the final "p",
        // matching the issue #101 repro (click just past the end of the step text).
        var step = MakeDefinedMatch("Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 16), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_cursor_on_step_keyword_resolves_Async()
    {
        // Character 4 is on "Given" itself, before the step text span starts at character 10.
        var step = MakeDefinedMatch("Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 4), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_cursor_on_previous_line_still_returns_empty_Async()
    {
        // Cursor on "Scenario: S" (line 1) must not resolve to the step on line 2.
        var step = MakeDefinedMatch("Steps.cs", csLine: 10, csColumn: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 1, 5), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Ambiguous step — multiple bindings ───────────────────────────────────

    /// <summary>
    /// Builds a StepBindingMatch whose MatchResult has two Ambiguous items pointing to
    /// different source locations — simulates a step matched by more than one step definition.
    /// </summary>
    private static StepBindingMatch MakeAmbiguousMatch(
        (string file, int line, int col)[] bindings,
        int startOffset = 33, int length = 6)
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, startOffset, length);

        var items = bindings
            .Select(b =>
            {
                var binding = new ProjectStepDefinitionBinding(
                    ScenarioBlock.Given,
                    new Regex("^a step$"),
                    null,
                    new ProjectBindingImplementation(
                        "AStep",
                        null,
                        new SourceLocation(b.file, b.line, b.col)));
                return MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
                                     .CloneToAmbiguousItem();
            })
            .ToArray();

        var result = MatchResult.CreateMultiMatch(items);
        return new StepBindingMatch(FeatureUri.ToString(), range, result);
    }

    [Fact]
    public async Task Handle_ambiguous_step_returns_all_binding_locations_Async()
    {
        var step = MakeAmbiguousMatch(new[]
        {
            ("/workspace/StepsA.cs", 10, 5),
            ("/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_ambiguous_step_each_location_points_to_correct_cs_file_Async()
    {
        var step = MakeAmbiguousMatch(new[]
        {
            ("/workspace/StepsA.cs", 10, 5),
            ("/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var paths = result!
            .Select(l => l.Location!.Uri.GetFileSystemPath())
            .ToArray();

        paths.Should().Contain("/workspace/StepsA.cs");
        paths.Should().Contain("/workspace/StepsB.cs");
    }

    [Fact]
    public async Task Handle_ambiguous_step_locations_are_zero_based_Async()
    {
        // SourceLocation line 10 col 5 (1-based) → LSP line 9 char 4 (0-based)
        var step = MakeAmbiguousMatch(new[]
        {
            ("/workspace/StepsA.cs", 10, 5),
            ("/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var rangeA = result!.First(l => l.Location!.Uri.GetFileSystemPath() == "/workspace/StepsA.cs")
                            .Location!.Range;
        rangeA.Start.Line.Should().Be(9);
        rangeA.Start.Character.Should().Be(4);

        var rangeB = result!.First(l => l.Location!.Uri.GetFileSystemPath() == "/workspace/StepsB.cs")
                            .Location!.Range;
        rangeB.Start.Line.Should().Be(19);
        rangeB.Start.Character.Should().Be(2);
    }

    // ── Undefined step at cursor ──────────────────────────────────────────────

    [Fact]
    public async Task Handle_undefined_step_at_cursor_returns_empty_Async()
    {
        var snapshot      = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range         = GherkinRange.FromPoint(snapshot, 33, 6);
        var undefinedStep = new StepBindingMatch(
            FeatureUri.ToString(), range, MatchResult.NoMatch);

        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { undefinedStep }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Owner resolution ──────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_uses_primary_owner_key_to_query_match_set_Async()
    {
        var ideScope = new LspIdeScope(Substitute.For<IIdeSupportLogger>());
        var project  = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = "/workspace",
                ProjectFile            = "C:/proj/A.csproj",
                ProjectFolder          = "/workspace",
                OutputAssemblyPath     = "/workspace/bin/A.dll",
                TargetFrameworkMoniker = "net8.0"
            },
            ideScope);

        _scopeManager.ResolvePrimaryOwner(FeatureUri).Returns(project);

        var owner = new ProjectOwner(project.ProjectFullName, project.TargetFrameworkMoniker);
        var step  = MakeDefinedMatch("Steps.cs", csLine: 5, csColumn: 1);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), owner, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        // Handler found the match set stored under the project owner key.
        result.Should().NotBeNull();
        result!.Should().ContainSingle();

        project.Dispose();
    }

    [Fact]
    public async Task Handle_uses_unknown_owner_key_when_no_primary_owner_Async()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        // Store under Unknown owner — handler should find it.
        var step = MakeDefinedMatch("Steps.cs", csLine: 5, csColumn: 1);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().Handle(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.Should().NotBeNull();
    }
}
