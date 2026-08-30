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
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

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
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    public HookMatchCountCodeLensHandlerTests()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(Array.Empty<FeatureBindingMatchSet>());
    }

    /// <summary>
    /// Builds the handler for a client identified by <paramref name="ide"/>. The default
    /// (<c>supportsCodeLensResolve: false</c>) is what EVERY shipped client gets today — the
    /// <c>codeLens/resolve</c> allowlist in <see cref="ClientIdeContext"/> is empty, so the eager
    /// path is the production path for VS, VS Code and Rider alike (issue #471).
    /// Pass <c>supportsCodeLensResolve: true</c> to exercise the deferred branch, which is real
    /// code kept ready for the first client that implements the resolve round trip.
    /// </summary>
    private HookMatchCountCodeLensHandler CreateSut(string ide = "visualstudio", bool supportsCodeLensResolve = false) =>
        new(_matchService, _scopeManager, _registryLookup,
            new ClientIdeContext(ide, supportsCodeLensResolve), _logger);

    private static CodeLensParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private static ProjectHookBinding MakeHook(
        HookType hookType, string csFile = "/workspace/Hooks.cs", int csLine = 5, int csColumn = 1)
        => new(
            new ProjectBindingImplementation("MyHook", parameterTypes: null, new SourceLocation(csFile, csLine, csColumn)),
            scope: null, hookType, hookOrder: null, error: null);

    // A scoped-but-matches-everything hook: distinct from MakeHook's unscoped (Scope == null)
    // case, which now renders "all scenarios" instead of a count (issue #403). Scoping to the
    // "F" feature title used throughout these fixtures matches every scenario without needing a
    // real tag on the feature.
    private static ProjectHookBinding MakeScopedHook(
        HookType hookType, string csFile = "/workspace/Hooks.cs", int csLine = 5, int csColumn = 1)
        => new(
            new ProjectBindingImplementation("MyHook", parameterTypes: null, new SourceLocation(csFile, csLine, csColumn)),
            scope: new BindingScope { FeatureTitle = "F" }, hookType, hookOrder: null, error: null);

    private FeatureBindingMatchSet BuildMatchSet(string text, ProjectBindingRegistry registry, string docId)
    {
        var parser = new IdeSupportTagParser(_parserLogger, _parserTelemetry, _configProvider);
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
        var hook = MakeScopedHook(HookType.BeforeScenario);
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
        var hook = MakeScopedHook(HookType.BeforeScenario);
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
    public async Task Handle_unscoped_hook_shows_all_scenarios_label_instead_of_a_count()
    {
        // #403: an unscoped hook (no [Scope] at all) matches every scenario in the project, so a
        // count would be unbounded/uninformative -- show a static label and skip the corpus walk.
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("all scenarios");
        result[0].Command!.Name.Should().Be("reqnroll.goToMatchingScenarios",
            "the click action must still resolve the full scenario list on demand");
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

    // ── Gated eager/deferred split + resolve (issue #471) ───────────────────────

    /// <summary>
    /// The regression guard for issue #471's final-review fix: the deferred path must NOT be
    /// active for any client the repo actually ships. Every one of these goes through the
    /// production <see cref="ClientIdeContext"/> constructor, i.e. the real allowlist lookup —
    /// so this fails the moment someone adds an entry without also shipping client-side
    /// <c>resolveCodeLens</c> support.
    /// </summary>
    [Theory]
    [InlineData("visualstudio")]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData(null)]
    public async Task Handle_computes_every_scoped_hook_lens_eagerly_for_all_shipped_clients(string? ide)
    {
        new ClientIdeContext(ide).SupportsCodeLensResolve.Should()
            .BeFalse("no shipped client implements the codeLens/resolve round trip yet");

        var hook = MakeScopedHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var sut = new HookMatchCountCodeLensHandler(
            _matchService, _scopeManager, _registryLookup, new ClientIdeContext(ide), _logger);
        var result = await sut.HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("1 scenario matched");
        result[0].Data.Should().BeNull();
    }

    [Fact]
    public async Task Handle_resolve_capable_client_defers_scoped_hooks_without_walking_the_corpus()
    {
        var hook = MakeScopedHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var result = await CreateSut(supportsCodeLensResolve: true)
            .HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command.Should().BeNull();
        result[0].Data.Should().NotBeNull();
        _matchService.DidNotReceive().GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>());
    }

    [Fact]
    public async Task Handle_resolve_capable_client_still_resolves_unscoped_hooks_eagerly()
    {
        // "all scenarios" needs no corpus walk (issue #403) -- no reason to defer it.
        var hook = MakeHook(HookType.BeforeScenario); // unscoped
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var result = await CreateSut(supportsCodeLensResolve: true)
            .HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Command!.Title.Should().Be("all scenarios");
    }

    [Fact]
    public async Task ResolveAsync_computes_the_scenario_count_from_the_lens_Data()
    {
        var hook = MakeScopedHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var placeholder = (await CreateSut(supportsCodeLensResolve: true)
            .HandleAsync(RequestFor(CsUri), CancellationToken.None))[0];
        placeholder.Command.Should().BeNull("the deferred branch must be the one under test here");

        var resolved = await CreateSut().ResolveAsync(placeholder, CancellationToken.None);

        resolved.Command!.Title.Should().Be("1 scenario matched");
        resolved.Command.Name.Should().Be("reqnroll.goToMatchingScenarios");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_a_non_actionable_lens_when_the_Data_is_malformed()
    {
        // Must NOT hand the client a clickable reqnroll.goToMatchingScenarios command built from
        // a fabricated "file:///unknown" URI (issue #471 final review).
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(4, 0), new Position(4, 0)),
            Data = new JObject { ["kind"] = "hookMatchCount" } // no uri/sourceFile/line/column
        };

        var resolved = await CreateSut().ResolveAsync(lens, CancellationToken.None);

        resolved.Command!.Name.Should().NotBe("reqnroll.goToMatchingScenarios");
        resolved.Command.Arguments.Should().BeNull("no URI is known, so nothing may be clickable");
        resolved.Command.Title.Should().Be("0 scenarios matched");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_a_non_actionable_lens_when_the_hook_is_gone()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(4, 0), new Position(4, 0)),
            Data = new JObject
            {
                ["kind"] = "hookMatchCount",
                ["uri"] = CsUri.ToString(),
                ["sourceFile"] = CsUri.GetFileSystemPath(),
                ["sourceLine"] = 5,
                ["sourceColumn"] = 1,
            }
        };

        var resolved = await CreateSut().ResolveAsync(lens, CancellationToken.None);

        resolved.Command!.Arguments.Should().BeNull("there is no hook left to navigate to");
        resolved.Command.Title.Should().Be("0 scenarios matched");
    }
}
