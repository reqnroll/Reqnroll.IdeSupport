using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;



using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Definition;

public class GoToHooksHandlerTests
{
    private readonly IDocumentBufferService        _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    // Feature text layout:
    //   Line 0 (offset  0): "Feature: F\n"        (11 chars)
    //   Line 1 (offset 11): "Scenario: S\n"       (12 chars)
    //   Line 2 (offset 23): "    Given a step\n"  (17 chars)
    private const string FeatureText = "Feature: F\nScenario: S\n    Given a step\n";

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private static readonly DocumentUri CsUri =
        DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    // DeveroomTag ranges relative to FeatureText:
    //   FeatureBlock covers the full text            [0, 40)
    //   ScenarioDefinitionBlock covers lines 1-2     [11, 40)
    //   StepBlock covers line 2                      [23, 40)
    private static readonly LspTextSnapshot Snapshot =
        new(FeatureUri.ToString(), 1, FeatureText);

    private static readonly DeveroomTag FeatureBlockTag = new(
        DeveroomTagTypes.FeatureBlock,
        new GherkinRange(Snapshot, 0, FeatureText.Length));

    private static readonly DeveroomTag ScenarioDefTag = new(
        DeveroomTagTypes.ScenarioDefinitionBlock,
        new GherkinRange(Snapshot, 11, 29));    // "Scenario: S\n    Given a step\n"

    private static readonly DeveroomTag StepBlockTag = new(
        DeveroomTagTypes.StepBlock,
        new GherkinRange(Snapshot, 23, 17));    // "    Given a step\n"

    private static readonly IReadOnlyList<DeveroomTag> AllTags =
        new[] { FeatureBlockTag, ScenarioDefTag, StepBlockTag };

    public GoToHooksHandlerTests()
    {
        // Default: Invalid registry
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.Invalid);

        SetupBuffer(FeatureUri, FeatureText, AllTags);
    }

    private GoToHooksHandler CreateSut() =>
        new(_bufferService, _registryLookup, _logger);

    private GoToHooksHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_bufferService, _registryLookup, _logger, telemetry);

    private static GoToHooksParams RequestAt(DocumentUri uri, int line, int character, bool ownLevelOnly = false) =>
        new()
        {
            TextDocument  = new TextDocumentIdentifier { Uri = uri },
            Position      = new Position(line, character),
            OwnLevelOnly  = ownLevelOnly,
        };

    private void SetupBuffer(
        DocumentUri uri, string text,
        IReadOnlyCollection<DeveroomTag>? tags = null)
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
        HookType hookType,
        string   csFile    = "Hooks.cs",
        int      csLine    = 10,
        int      csColumn  = 5,
        int?     hookOrder = null)
        => new(
            new ProjectBindingImplementation(
                "MyHook",
                parameterTypes: null,
                new SourceLocation(csFile, csLine, csColumn)),
            scope: null,
            hookType,
            hookOrder,
            error: null);

    private static ProjectBindingRegistry RegistryWith(params ProjectHookBinding[] hooks)
        => ProjectBindingRegistry.FromBindings(
            Array.Empty<ProjectStepDefinitionBinding>(),
            hooks);

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_feature_uri_returns_empty_Async()
    {
        var result = await CreateSut().HandleAsync(
            RequestAt(CsUri, 0, 0), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_missing_buffer_returns_empty_Async()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/untracked.feature");
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(
            RequestAt(uri, 0, 0), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_buffer_with_null_tags_returns_empty_Async()
    {
        SetupBuffer(FeatureUri, FeatureText, tags: null);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_invalid_registry_returns_empty_Async()
    {
        // Default setup already returns Invalid; just verify the handler short-circuits.
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(ProjectBindingRegistry.Invalid);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_cursor_outside_all_tags_returns_empty_Async()
    {
        // Use a buffer where no tags cover any position
        SetupBuffer(FeatureUri, FeatureText, tags: Array.Empty<DeveroomTag>());

        _registryLookup.GetRegistryForUri(FeatureUri)
                       .Returns(RegistryWith(MakeHook(HookType.BeforeScenario)));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    // ── Context level — Feature line ──────────────────────────────────────────

    [Fact]
    public async Task Handle_feature_line_returns_feature_level_hooks_Async()
    {
        var hook = MakeHook(HookType.BeforeFeature);
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(hook));

        // Cursor at line 0, char 5 → offset 5 → inside FeatureBlock, outside ScenarioDefBlock
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5), CancellationToken.None);

        result.Hooks.Should().ContainSingle(h => h.HookType == "BeforeFeature");
    }

    [Fact]
    public async Task Handle_feature_line_excludes_scenario_and_step_hooks_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeTestRun),
            MakeHook(HookType.BeforeFeature),
            MakeHook(HookType.BeforeScenario),   // should be excluded
            MakeHook(HookType.BeforeStep));       // should be excluded
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5), CancellationToken.None);

        result.Hooks.Should().HaveCount(2);
        result.Hooks.Select(h => h.HookType).Should()
            .BeEquivalentTo(["BeforeTestRun", "BeforeFeature"]);
    }

    // ── Context level — Scenario line ────────────────────────────────────────

    [Fact]
    public async Task Handle_scenario_line_returns_scenario_and_feature_level_hooks_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),
            MakeHook(HookType.BeforeScenario),
            MakeHook(HookType.BeforeStep));    // should be excluded
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        // Cursor at line 1, char 4 → offset 15 → inside ScenarioDefBlock, outside StepBlock
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        result.Hooks.Should().HaveCount(2);
        result.Hooks.Select(h => h.HookType).Should()
            .BeEquivalentTo(["BeforeFeature", "BeforeScenario"]);
    }

    [Fact]
    public async Task Handle_cursor_at_end_of_scenario_line_still_resolves_scenario_context_Async()
    {
        // Line 1 "Scenario: S" is 11 chars — character 11 is one past the final "S".
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),
            MakeHook(HookType.BeforeScenario),
            MakeHook(HookType.BeforeStep));    // should be excluded
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 11), CancellationToken.None);

        result.Hooks.Should().HaveCount(2);
        result.Hooks.Select(h => h.HookType).Should()
            .BeEquivalentTo(["BeforeFeature", "BeforeScenario"]);
    }

    // ── Context level — Step line ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_step_line_returns_all_hook_types_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),
            MakeHook(HookType.BeforeScenario),
            MakeHook(HookType.BeforeScenarioBlock),
            MakeHook(HookType.BeforeStep));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        // Cursor at line 2, char 7 → offset 30 → inside StepBlock
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 7), CancellationToken.None);

        result.Hooks.Should().HaveCount(4);
    }

    [Fact]
    public async Task Handle_cursor_at_end_of_step_line_still_resolves_step_context_Async()
    {
        // Line 2 "    Given a step" is 17 chars — character 17 is one past the final "p",
        // i.e. exactly at StepBlockTag.Range.End (offset 40). Matches issue #106 repro.
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),
            MakeHook(HookType.BeforeScenario),
            MakeHook(HookType.BeforeScenarioBlock),
            MakeHook(HookType.BeforeStep));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 17), CancellationToken.None);

        result.Hooks.Should().HaveCount(4);
    }

    [Fact]
    public async Task Handle_step_line_excludes_hooks_not_in_step_level_set_Async()
    {
        var registry = RegistryWith(MakeHook(HookType.BeforeTestThread));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        // BeforeTestThread is not in the applicable set for any level
        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 7), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    // ── ownLevelOnly (hook-count CodeLens click-through — issue #372 follow-up) ──────────

    [Fact]
    public async Task Handle_scenario_line_with_ownLevelOnly_excludes_feature_hooks_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),   // excluded: not native to Scenario level
            MakeHook(HookType.BeforeScenario));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4, ownLevelOnly: true), CancellationToken.None);

        result.Hooks.Should().ContainSingle(h => h.HookType == "BeforeScenario");
    }

    [Fact]
    public async Task Handle_step_line_with_ownLevelOnly_excludes_feature_and_scenario_hooks_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeFeature),        // excluded
            MakeHook(HookType.BeforeScenario),        // excluded
            MakeHook(HookType.BeforeScenarioBlock),   // included: native to Step level
            MakeHook(HookType.BeforeStep));           // included
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 2, 7, ownLevelOnly: true), CancellationToken.None);

        result.Hooks.Select(h => h.HookType).Should()
            .BeEquivalentTo(["BeforeScenarioBlock", "BeforeStep"]);
    }

    [Fact]
    public async Task Handle_feature_line_with_ownLevelOnly_is_unaffected_Async()
    {
        // Feature is the outermost level, so own-level and cumulative sets are identical.
        var registry = RegistryWith(MakeHook(HookType.BeforeFeature));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 0, 5, ownLevelOnly: true), CancellationToken.None);

        result.Hooks.Should().ContainSingle(h => h.HookType == "BeforeFeature");
    }

    // ── Location conversion (1-based → 0-based) ──────────────────────────────

    [Fact]
    public async Task Handle_location_start_is_converted_to_zero_based_Async()
    {
        // SourceLocation: line 10 (1-based), column 5 (1-based)
        // Expected response: startLine 9 (0-based), startChar 4 (0-based)
        var hook = MakeHook(HookType.BeforeScenario, csFile: "/workspace/Hooks.cs", csLine: 10, csColumn: 5);
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(hook));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        var loc = result.Hooks.Should().ContainSingle().Subject;
        loc.StartLine.Should().Be(9);
        loc.StartChar.Should().Be(4);
    }

    [Fact]
    public async Task Handle_location_uri_is_file_uri_for_cs_path_Async()
    {
        var hook = MakeHook(HookType.BeforeScenario, csFile: "/workspace/Hooks.cs");
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(hook));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        var loc = result.Hooks.Should().ContainSingle().Subject;
        loc.Uri.Should().Contain("Hooks.cs");
    }

    [Fact]
    public async Task Handle_location_carries_hook_type_and_method_name_Async()
    {
        var hook = new ProjectHookBinding(
            new ProjectBindingImplementation(
                "SetUpDatabase", null,
                new SourceLocation("/workspace/Hooks.cs", 5, 1)),
            scope: null,
            HookType.BeforeScenario,
            hookOrder: 500,
            error: null);
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(hook));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        var loc = result.Hooks.Should().ContainSingle().Subject;
        loc.HookType.Should().Be("BeforeScenario");
        loc.MethodName.Should().Be("SetUpDatabase");
        loc.HookOrder.Should().Be(500);
    }

    [Fact]
    public async Task Handle_hook_without_source_location_is_excluded_Async()
    {
        // A hook with null SourceLocation (e.g. error binding) should not appear in results.
        var badHook = new ProjectHookBinding(
            new ProjectBindingImplementation("BadHook", null, null!),
            scope: null,
            HookType.BeforeScenario,
            hookOrder: null,
            error: null);
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(RegistryWith(badHook));

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        result.Hooks.Should().BeEmpty();
    }

    // ── Result ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_results_are_ordered_by_hook_type_then_order_Async()
    {
        var registry = RegistryWith(
            MakeHook(HookType.BeforeScenario, hookOrder: 200),
            MakeHook(HookType.BeforeFeature,  hookOrder: 100),
            MakeHook(HookType.BeforeScenario, hookOrder: 100, csFile: "/workspace/Other.cs"));
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(registry);

        var result = await CreateSut().HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        result.Hooks.Should().HaveCount(3);
        result.Hooks[0].HookType.Should().Be("BeforeFeature");
        result.Hooks[1].HookOrder.Should().Be(100);    // BeforeScenario order:100 before order:200
        result.Hooks[2].HookOrder.Should().Be(200);
    }

    // ── Telemetry ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_emits_command_telemetry()
    {
        SetupBuffer(FeatureUri, FeatureText, AllTags);
        _registryLookup.GetRegistryForUri(FeatureUri)
            .Returns(RegistryWith(MakeHook(HookType.BeforeScenario)));

        var telemetry = Substitute.For<ILspTelemetryService>();
        var result = await CreateSutWithTelemetry(telemetry).HandleAsync(
            RequestAt(FeatureUri, 1, 4), CancellationToken.None);

        telemetry.Received(1).SendEvent("GoToHook command executed", Arg.Any<Dictionary<string, object?>>());
    }
}
