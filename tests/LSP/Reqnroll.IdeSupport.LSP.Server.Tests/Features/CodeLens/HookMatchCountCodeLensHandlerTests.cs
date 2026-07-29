using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class HookMatchCountCodeLensHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri CsUri      = DocumentUri.FromFileSystemPath("/workspace/Hooks.cs");
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private readonly IIdeSupportLogger _parserLogger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _parserTelemetry = Substitute.For<ITelemetryService>();
    private readonly IDeveroomConfigurationProvider _configProvider = Substitute.For<IDeveroomConfigurationProvider>();

    public HookMatchCountCodeLensHandlerTests()
    {
        _configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(Array.Empty<FeatureBindingMatchSet>());
    }

    private HookMatchCountCodeLensHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger);

    private static CodeLensParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private static ProjectHookBinding MakeHook(
        HookType hookType, string csFile = "/workspace/Hooks.cs", int csLine = 5, int csColumn = 1)
        => new(
            new ProjectBindingImplementation("MyHook", parameterTypes: null, new SourceLocation(csFile, csLine, csColumn)),
            scope: null, hookType, hookOrder: null, error: null);

    private FeatureBindingMatchSet BuildMatchSet(string text, ProjectBindingRegistry registry, string docId)
    {
        var parser = new DeveroomTagParser(_parserLogger, _parserTelemetry, _configProvider);
        var tags   = parser.Parse(new LspTextSnapshot(docId, 1, text), registry);
        return FeatureBindingMatchSet.FromTags(docId, 1, registry.Version, tags);
    }

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_cs_uri_returns_empty()
    {
        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_invalid_registry_returns_empty()
    {
        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_registry_with_no_hooks_returns_empty()
    {
        _registryLookup.GetRegistryForUri(CsUri)
            .Returns(ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>()));

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Counting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_hook_matching_one_scenario_reports_singular_title()
    {
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("1 scenario matched");
    }

    [Fact]
    public async Task Handle_hook_matching_multiple_scenarios_reports_plural_title()
    {
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        const string feature = "Feature: F\nScenario: S1\n    Given a step\nScenario: S2\n    Given a step\n";
        var matchSet = BuildMatchSet(feature, registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("2 scenarios matched");
    }

    [Fact]
    public async Task Handle_hook_matching_no_scenarios_still_renders_the_lens_at_zero()
    {
        // #373 decided semantics: zero is the most actionable case (likely a dead/mistyped
        // hook scope), so this deliberately diverges from #269's "skip empty" convention.
        var hook = new ProjectHookBinding(
            new ProjectBindingImplementation("MyHook", null, new SourceLocation("/workspace/Hooks.cs", 5, 1)),
            scope: new BindingScope { Tag = Reqnroll.IdeSupport.LSP.Core.TagExpressions.ReqnrollTagExpressionParser.CreateTagLiteral("@nonexistent") },
            HookType.BeforeScenario, hookOrder: null, error: null);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("0 scenarios matched");
    }

    [Fact]
    public async Task Handle_test_run_scoped_hook_is_skipped_entirely()
    {
        var hook = MakeHook(HookType.BeforeTestRun);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().BeEmpty("BeforeTestRun/AfterTestRun hooks have no per-scenario concept");
    }

    // ── Command wiring ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_lens_command_name_is_goToMatchingScenarios()
    {
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result[0].Command!.Name.Should().Be("reqnroll.goToMatchingScenarios");
    }

    [Fact]
    public async Task Handle_lens_range_and_arguments_are_at_the_hook_attribute_zero_based()
    {
        var hook = MakeHook(HookType.BeforeScenario, csLine: 10, csColumn: 5);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        // SourceFileLine 10 (1-based) -> LSP line 9; SourceFileColumn 5 -> LSP character 4.
        result[0].Range!.Start.Line.Should().Be(9);
        result[0].Range!.Start.Character.Should().Be(4);

        var args = (JArray)result[0].Command!.Arguments!;
        ((JValue)args[0]).Value.Should().Be(CsUri.ToString());
        ((JValue)args[1]).Value.Should().Be(9);
        ((JValue)args[2]).Value.Should().Be(4);
    }

    // ── Multiple hooks / dedup ────────────────────────────────────────────────

    [Fact]
    public async Task Handle_two_hooks_in_same_file_returns_two_lenses()
    {
        var h1 = MakeHook(HookType.BeforeScenario, csLine: 5, csColumn: 1);
        var h2 = MakeHook(HookType.AfterScenario,  csLine: 9, csColumn: 1);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { h1, h2 });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_duplicate_attribute_location_emits_only_one_lens()
    {
        var h1 = MakeHook(HookType.BeforeScenario, csLine: 5, csColumn: 1);
        var h2 = MakeHook(HookType.BeforeScenario, csLine: 5, csColumn: 1);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { h1, h2 });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle("duplicate attribute locations should be deduplicated");
    }

    [Fact]
    public async Task Handle_hook_in_different_file_is_excluded()
    {
        var hook = MakeHook(HookType.BeforeScenario, csFile: "/workspace/OtherHooks.cs");
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Mixed-file coexistence (issue #373) ─────────────────────────────────────

    [Fact]
    public async Task Handle_file_with_both_hook_and_step_bindings_only_returns_hook_lenses()
    {
        // A single [Binding] class routinely mixes step-binding and hook-binding methods in one
        // .cs file -- this handler must filter to registry.Hooks and ignore step bindings sourced
        // from the same file, coexisting with StepCodeLensHandler's separate result set (combined
        // upstream in LanguageServerOptionsExtensions, not here).
        var hook = MakeHook(HookType.BeforeScenario, csLine: 5, csColumn: 1);
        var stepBinding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new System.Text.RegularExpressions.Regex("^my step$"),
            scope: null,
            implementation: new ProjectBindingImplementation(
                "MyStep", null, new SourceLocation("/workspace/Hooks.cs", 15, 1)));
        var registry = ProjectBindingRegistry.FromBindings(new[] { stepBinding }, new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle("only the hook binding should produce a lens from this handler");
        result[0].Range!.Start.Line.Should().Be(4);
    }
}
