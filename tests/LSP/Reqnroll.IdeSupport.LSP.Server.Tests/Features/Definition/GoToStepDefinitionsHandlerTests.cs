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
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Definition;

public class GoToStepDefinitionsHandlerTests
{
    private BindingMatchService             _matchService  = new();
    private readonly IDocumentBufferService     _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager  _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger            _logger        = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE          _fileSystem    = new FileSystemForIDE();

    // Feature layout — same as FeatureDefinitionHandlerTests:
    // Line 0: "Feature: F"         offsets  0–9  (\n at 10)
    // Line 1: "Scenario: S"        offsets 11–21 (\n at 22)
    // Line 2: "    Given a step"   offsets 23–38 (\n at 39)
    //   step text starts at offset 33, LSP position line=2 character=10
    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n";

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    public GoToStepDefinitionsHandlerTests()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
                     .Returns((LspReqnrollProject?)null);

        SetupBuffer(FeatureUri, FeatureText);
    }

    private GoToStepDefinitionsHandler CreateSut() =>
        new(_matchService, _bufferService, _scopeManager, _logger, _fileSystem);

    private GoToStepDefinitionsHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_matchService, _bufferService, _scopeManager, _logger, _fileSystem, telemetry);

    private static TextDocumentPositionParams RequestAt(DocumentUri uri, int line, int character) =>
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

    private static StepBindingMatch MakeMatch(
        string method, ScenarioBlock stepType, string csFile, int csLine, int csCol,
        MatchResultType resultType = MatchResultType.Defined,
        int startOffset = 33, int length = 6)
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, startOffset, length);

        var binding = new ProjectStepDefinitionBinding(
            stepType,
            new Regex("^a step$"),
            null,
            new ProjectBindingImplementation(method, null, new SourceLocation(csFile, csLine, csCol)));

        var item = resultType == MatchResultType.Ambiguous
            ? MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch).CloneToAmbiguousItem()
            : MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);

        return new StepBindingMatch(
            FeatureUri.ToString(), range,
            MatchResult.CreateMultiMatch(new[] { item }));
    }

    private static StepBindingMatch MakeAmbiguousMatch(
        (string method, string file, int line, int col)[] bindings,
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
                    new ProjectBindingImplementation(b.method, null,
                        new SourceLocation(b.file, b.line, b.col)));
                return MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
                                     .CloneToAmbiguousItem();
            })
            .ToArray();

        return new StepBindingMatch(
            FeatureUri.ToString(), range,
            MatchResult.CreateMultiMatch(items));
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_non_feature_uri_returns_empty_Async()
    {
        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 0, 0), CancellationToken.None);

        result.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_missing_buffer_returns_empty_Async()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/untracked.feature");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(
            RequestAt(uri, 2, 10), CancellationToken.None);

        result.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_no_match_set_cached_returns_empty_Async()
    {
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_cursor_not_on_step_returns_empty_Async()
    {
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1,
            Array.Empty<StepBindingMatch>()));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 0), CancellationToken.None);

        result.StepDefinitions.Should().BeEmpty();
    }

    // ── Single defined binding ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_defined_step_returns_one_location_Async()
    {
        var step = MakeMatch("CalculatorSteps.GivenAStep", ScenarioBlock.Given,
                             "/workspace/Steps.cs", csLine: 10, csCol: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_location_uri_points_to_cs_file_Async()
    {
        var step = MakeMatch("Steps.GivenAStep", ScenarioBlock.Given,
                             "/workspace/Steps.cs", csLine: 10, csCol: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        DocumentUri.From(result.StepDefinitions[0].Uri)
            .GetFileSystemPath().Should().Be("/workspace/Steps.cs");
    }

    [Fact]
    public async Task HandleAsync_location_is_zero_based_Async()
    {
        // SourceLocation line 10, col 5 (1-based) → response line 9, char 4 (0-based)
        var step = MakeMatch("Steps.GivenAStep", ScenarioBlock.Given,
                             "/workspace/Steps.cs", csLine: 10, csCol: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions[0].StartLine.Should().Be(9);
        result.StepDefinitions[0].StartChar.Should().Be(4);
    }

    [Fact]
    public async Task HandleAsync_returns_step_type_Async()
    {
        var step = MakeMatch("Steps.WhenSomething", ScenarioBlock.When,
                             "/workspace/Steps.cs", csLine: 5, csCol: 1);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions[0].StepType.Should().Be("When");
    }

    [Fact]
    public async Task HandleAsync_returns_method_name_Async()
    {
        var step = MakeMatch("CalculatorSteps.GivenTheFirstNumberIs", ScenarioBlock.Given,
                             "/workspace/Steps.cs", csLine: 5, csCol: 1);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions[0].MethodName.Should().Be("CalculatorSteps.GivenTheFirstNumberIs");
    }

    // ── Ambiguous binding — multiple locations ────────────────────────────────

    [Fact]
    public async Task HandleAsync_ambiguous_step_returns_all_bindings_Async()
    {
        var step = MakeAmbiguousMatch(new[]
        {
            ("StepsA.GivenSomething", "/workspace/StepsA.cs", 10, 5),
            ("StepsB.GivenSomethingElse", "/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_ambiguous_step_each_location_has_method_name_Async()
    {
        var step = MakeAmbiguousMatch(new[]
        {
            ("StepsA.GivenSomething", "/workspace/StepsA.cs", 10, 5),
            ("StepsB.GivenSomethingElse", "/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var methods = result.StepDefinitions.Select(d => d.MethodName).ToArray();
        methods.Should().Contain("StepsA.GivenSomething");
        methods.Should().Contain("StepsB.GivenSomethingElse");
    }

    [Fact]
    public async Task HandleAsync_ambiguous_step_each_location_has_correct_uri_Async()
    {
        var step = MakeAmbiguousMatch(new[]
        {
            ("StepsA.GivenSomething",    "/workspace/StepsA.cs", 10, 5),
            ("StepsB.GivenSomethingElse", "/workspace/StepsB.cs", 20, 3),
        });
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        var paths = result.StepDefinitions
            .Select(d => DocumentUri.From(d.Uri).GetFileSystemPath())
            .ToArray();

        paths.Should().Contain("/workspace/StepsA.cs");
        paths.Should().Contain("/workspace/StepsB.cs");
    }

    // ── Telemetry ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_emits_command_telemetry_on_success()
    {
        var step = MakeMatch("CalculatorSteps.GivenAStep", ScenarioBlock.Given,
                             "/workspace/Steps.cs", csLine: 10, csCol: 5);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1,
            new List<StepBindingMatch> { step }));

        var telemetry = Substitute.For<ILspTelemetryService>();
        var result = await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(FeatureUri, 2, 10), CancellationToken.None);

        result.StepDefinitions.Should().NotBeEmpty();
        telemetry.Received(1).SendEvent(
            "GoToStepDefinition command executed",
            Arg.Is<Dictionary<string, object?>>(d => false.Equals(d["GenerateSnippet"])));
    }

    [Fact]
    public async Task HandleAsync_does_not_emit_telemetry_on_non_feature_file()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(CsUri, 0, 0), CancellationToken.None);

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    [Fact]
    public async Task HandleAsync_does_not_emit_telemetry_when_no_document_buffer()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        var unknownUri = DocumentUri.FromFileSystemPath("/workspace/unknown.feature");
        var result = await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(unknownUri, 0, 0), CancellationToken.None);

        result.StepDefinitions.Should().BeEmpty();
        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
