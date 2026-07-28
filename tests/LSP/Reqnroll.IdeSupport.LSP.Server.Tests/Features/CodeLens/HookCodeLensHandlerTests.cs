using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class HookCodeLensHandlerTests
{
    private readonly IDocumentBufferService        _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    // Feature text layout (same fixture shape as GoToHooksHandlerTests):
    //   Line 0 (offset  0): "Feature: F\n"        (11 chars)
    //   Line 1 (offset 11): "Scenario: S\n"       (12 chars)
    //   Line 2 (offset 23): "    Given a step\n"  (17 chars)
    private const string FeatureText = "Feature: F\nScenario: S\n    Given a step\n";

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri      = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private static readonly LspTextSnapshot Snapshot = new(FeatureUri.ToString(), 1, FeatureText);

    private static readonly DeveroomTag FeatureBlockTag = new(
        DeveroomTagTypes.FeatureBlock, new GherkinRange(Snapshot, 0, FeatureText.Length));

    private static readonly DeveroomTag ScenarioDefTag = new(
        DeveroomTagTypes.ScenarioDefinitionBlock, new GherkinRange(Snapshot, 11, 29));

    private static readonly DeveroomTag StepBlockTag = new(
        DeveroomTagTypes.StepBlock, new GherkinRange(Snapshot, 23, 17));

    private static readonly IReadOnlyList<DeveroomTag> AllTags =
        new[] { FeatureBlockTag, ScenarioDefTag, StepBlockTag };

    public HookCodeLensHandlerTests()
    {
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.Invalid);

        SetupBuffer(FeatureUri, FeatureText, AllTags);
    }

    private HookCodeLensHandler CreateSut() => new(_bufferService, _registryLookup, _logger);

    private static CodeLensParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

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

    private static ProjectHookBinding MakeHook(
        HookType hookType, string csFile = "Hooks.cs", int csLine = 10, int csColumn = 5, int? hookOrder = null)
        => new(
            new ProjectBindingImplementation("MyHook", parameterTypes: null, new SourceLocation(csFile, csLine, csColumn)),
            scope: null, hookType, hookOrder, error: null);

    private static ProjectBindingRegistry RegistryWith(params ProjectHookBinding[] hooks)
        => ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), hooks);

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_feature_uri_returns_empty()
    {
        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_missing_buffer_returns_empty()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/untracked.feature");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(RequestFor(uri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_buffer_with_null_tags_returns_empty()
    {
        SetupBuffer(FeatureUri, FeatureText, tags: null);
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeFeature)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_invalid_registry_returns_empty()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(ProjectBindingRegistry.Invalid);

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_registry_with_no_hooks_returns_empty()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith());

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── One lens per applicable line ─────────────────────────────────────────

    [Fact]
    public async Task Handle_feature_scoped_hook_produces_lenses_on_all_three_lines()
    {
        // BeforeFeature is applicable at Feature, Scenario, and Step level (cumulative).
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeFeature)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().HaveCount(3);
        result.Select(l => l.Range!.Start.Line).Should().BeEquivalentTo([0, 1, 2]);
    }

    [Fact]
    public async Task Handle_scenario_scoped_hook_only_produces_lenses_on_scenario_and_step_lines()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeScenario)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(l => l.Range!.Start.Line).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task Handle_step_scoped_hook_only_produces_a_lens_on_the_step_line()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeStep)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Range!.Start.Line.Should().Be(2);
    }

    [Fact]
    public async Task Handle_no_applicable_hooks_returns_empty()
    {
        // BeforeTestThread is not in the applicable set for any level.
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeTestThread)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Title / command wiring ────────────────────────────────────────────────

    [Fact]
    public async Task Handle_single_hook_uses_singular_title()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeStep)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("1 hook");
    }

    [Fact]
    public async Task Handle_multiple_hooks_uses_plural_title()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(
            MakeHook(HookType.BeforeStep), MakeHook(HookType.AfterStep)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("2 hooks");
    }

    [Fact]
    public async Task Handle_lens_command_name_is_goToHooks()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeStep)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result[0].Command!.Name.Should().Be("reqnroll.goToHooks");
    }

    [Fact]
    public async Task Handle_lens_command_arguments_carry_uri_line_and_character()
    {
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(MakeHook(HookType.BeforeStep)));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        var args = (JArray)result[0].Command!.Arguments!;
        ((JValue)args[0]).Value.Should().Be(FeatureUri.ToString());
        ((JValue)args[1]).Value.Should().Be(2);
        ((JValue)args[2]).Value.Should().Be(0); // StepBlockTag.Range.Start is the line's start offset, not past the leading whitespace
    }
}
