#nullable enable

using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.InlayHints;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Server.Features.InlayHints;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.InlayHints;

public class InlayHintHandlerTests
{
    private readonly BindingMatchService _matchService = new();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IGherkinInlayHintService _hintService = new GherkinInlayHintService();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n    And another step\n";

    public InlayHintHandlerTests()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
                     .Returns((LspReqnrollProject?)null);
    }

    private InlayHintHandler CreateSut() => new(_matchService, _scopeManager, _hintService, _logger);

    private static InlayHintParams RequestFor(DocumentUri uri, LspRange? range = null) => new()
    {
        TextDocument = new TextDocumentIdentifier { Uri = uri },
        Range = range ?? new LspRange(new Position(0, 0), new Position(999, 0)),
    };

    private static StepBindingMatch MakeMatch(string method, int startOffset, int length, string pattern = "a step")
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range = GherkinRange.FromPoint(snapshot, startOffset, length);
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(pattern) + "$"),
            null,
            new ProjectBindingImplementation(method, new[] { "int" }, new SourceLocation("Steps.cs", 5, 1)));
        var item = MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);
        return new StepBindingMatch(FeatureUri.ToString(), range, MatchResult.CreateMultiMatch(new[] { item }));
    }

    [Fact]
    public async Task Handle_returns_empty_when_no_match_set_cached()
    {
        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result!.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_returns_a_hint_positioned_at_the_end_of_the_step_range()
    {
        // "    Given a step" -> step text "a step" starts at offset 33
        var step = MakeMatch("N.CalculatorSteps.GivenAStep", startOffset: 33, length: 6);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step }));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        var hint = result!.Should().ContainSingle().Subject;
        hint.Position.Line.Should().Be(2);
        hint.Label.ToString().Should().Contain("CalculatorSteps.GivenAStep");
        hint.Tooltip.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_only_returns_hints_that_intersect_the_requested_range()
    {
        var step1 = MakeMatch("N.S1", startOffset: 33, length: 6, pattern: "a step");
        var step2 = MakeMatch("N.S2", startOffset: 48, length: 12, pattern: "another step");
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step1, step2 }));

        // Only line 2 (the first step) is in view.
        var result = await CreateSut().HandleAsync(
            RequestFor(FeatureUri, new LspRange(new Position(0, 0), new Position(2, 100))),
            CancellationToken.None);

        var hint = result!.Should().ContainSingle().Subject;
        hint.Position.Line.Should().Be(2);
    }
}
