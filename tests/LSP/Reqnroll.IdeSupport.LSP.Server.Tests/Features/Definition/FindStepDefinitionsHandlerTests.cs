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
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Definition;

/// <summary>
/// <c>reqnroll/findStepDefinitions</c> (issue #757): the bindings of the step at the caret, with the
/// class/method/expression detail Visual Studio's results list shows. Step resolution itself is
/// shared with <see cref="DefinitionHandler"/> and covered by <see cref="DefinitionHandlerTests"/>.
/// </summary>
public class FindStepDefinitionsHandlerTests
{
    private readonly BindingMatchService       _matchService  = new();
    private readonly IDocumentBufferService    _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger         _logger        = Substitute.For<IIdeSupportLogger>();

    // Line 2 is "    Given a step"; the step text "a step" starts at offset 33 (line 2, character 10).
    private const string FeatureText = "Feature: F\nScenario: S\n    Given a step\n";

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public FindStepDefinitionsHandlerTests()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        var buf = new DocumentBuffer(FeatureUri, 1, FeatureText);
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(x => { x[1] = buf; return true; });
    }

    private FindStepDefinitionsHandler CreateSut(ILspTelemetryService? telemetry = null) =>
        new(_matchService, _bufferService, _scopeManager, _logger, new FileSystemForIDE(), telemetry);

    private static TextDocumentPositionParams RequestAt(int line, int character) => new()
    {
        TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
        Position     = new Position(line, character),
    };

    private static MatchResultItem Binding(string method, SourceLocation location, string? expression) =>
        MatchResultItem.CreateMatch(
            new ProjectStepDefinitionBinding(
                ScenarioBlock.Given, new Regex("^a step$"), null,
                new ProjectBindingImplementation(method, null, location),
                specifiedExpression: expression),
            ParameterMatch.NotMatch);

    private void StoreStep(params MatchResultItem[] items)
    {
        var range = GherkinRange.FromPoint(new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText), 33, 6);
        _matchService.Store(new FeatureBindingMatchSet(
            FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1,
            new[] { new StepBindingMatch(FeatureUri.ToString(), range, MatchResult.CreateMultiMatch(items)) }));
    }

    [Fact]
    public async Task No_step_at_the_caret_returns_no_items_Async()
    {
        StoreStep(Binding("Steps.AStep", new SourceLocation("/workspace/Steps.cs", 10, 5), "a step"));

        var response = await CreateSut().HandleAsync(RequestAt(1, 5), CancellationToken.None);

        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_binding_of_an_ambiguous_step_is_reported_with_its_class_method_and_expression_Async()
    {
        StoreStep(
            Binding("Calc.Steps.GivenAStep(int)", new SourceLocation("/workspace/Steps.cs", 10, 5), "a step").CloneToAmbiguousItem(),
            Binding("Calc.OtherSteps.AnyStep", new SourceLocation("/workspace/Other.cs", 20, 9), "a {word}").CloneToAmbiguousItem());

        var response = await CreateSut().HandleAsync(RequestAt(2, 10), CancellationToken.None);

        response.Items.Select(i => (i.ClassName, i.MethodName, i.BindingExpression, i.SourceFile, i.SourceLine, i.SourceChar, i.IsResolved))
            .Should().Equal(
                ("Steps", "GivenAStep", "a step", "/workspace/Steps.cs", 9, 4, true),
                ("OtherSteps", "AnyStep", "a {word}", "/workspace/Other.cs", 19, 8, true));
    }

    [Fact]
    public async Task A_method_name_style_binding_has_no_expression_Async()
    {
        StoreStep(Binding("Steps.Given_a_step", new SourceLocation("/workspace/Steps.cs", 10, 5), expression: null));

        var response = await CreateSut().HandleAsync(RequestAt(2, 10), CancellationToken.None);

        response.Items.Should().ContainSingle().Which.BindingExpression.Should().BeNull();
    }

    [Fact]
    public async Task Each_binding_reports_its_step_definition_type_Async()
    {
        StoreStep(Binding("Steps.AStep", new SourceLocation("/workspace/Steps.cs", 10, 5), "a step"));

        var response = await CreateSut().HandleAsync(RequestAt(2, 10), CancellationToken.None);

        response.Items.Single().StepDefinitionType.Should().Be("Given");
    }

    [Fact]
    public async Task A_binding_whose_source_is_not_on_this_machine_is_reported_as_unresolved_Async()
    {
        // Unlike textDocument/definition, which drops it: the client needs the row to say why it
        // cannot be navigated (issue #540).
        StoreStep(Binding("Steps.AStep", SourceLocation.Unresolved("/workspaces/host/Steps.cs", 10, 5), "a step"));

        var item = (await CreateSut().HandleAsync(RequestAt(2, 10), CancellationToken.None)).Items.Should().ContainSingle().Subject;

        item.IsResolved.Should().BeFalse();
        item.SourceFile.Should().BeNull();
        item.RecordedSourceFile.Should().Be("/workspaces/host/Steps.cs");
    }

    [Fact]
    public async Task Project_name_is_left_unset_Async()
    {
        StoreStep(Binding("Steps.AStep", new SourceLocation("/workspace/Steps.cs", 10, 5), "a step"));

        var response = await CreateSut().HandleAsync(RequestAt(2, 10), CancellationToken.None);

        response.Items.Single().ProjectName.Should().BeNull();
    }

    [Fact]
    public async Task Sends_the_go_to_step_definition_telemetry_counting_navigable_bindings_Async()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        StoreStep(
            Binding("Steps.Local", new SourceLocation("/workspace/Steps.cs", 10, 5), "a step").CloneToAmbiguousItem(),
            Binding("Steps.Foreign", SourceLocation.Unresolved("/workspaces/host/Other.cs", 20, 1), "a step").CloneToAmbiguousItem());

        await CreateSut(telemetry).HandleAsync(RequestAt(2, 10), CancellationToken.None);

        telemetry.Received(1).SendEvent(
            "GoToStepDefinition command executed",
            Arg.Is<Dictionary<string, object?>>(p => (int)p["LocationCount"]! == 1));
    }
}
