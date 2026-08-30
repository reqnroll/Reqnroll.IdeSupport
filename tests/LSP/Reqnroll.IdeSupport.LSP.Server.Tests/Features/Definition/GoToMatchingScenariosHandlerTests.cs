using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TagExpressions;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Definition;

public class GoToMatchingScenariosHandlerTests
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

    public GoToMatchingScenariosHandlerTests()
    {
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(Array.Empty<FeatureBindingMatchSet>());
    }

    private GoToMatchingScenariosHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger);

    private static TextDocumentPositionParams RequestAt(DocumentUri uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position     = new Position(line, character),
        };

    private static ProjectHookBinding MakeHook(
        HookType hookType, string csFile = "/workspace/Hooks.cs", int csLine = 5, int csColumn = 1)
        => new(
            new ProjectBindingImplementation("MyHook", parameterTypes: null, new SourceLocation(csFile, csLine, csColumn)),
            scope: null, hookType, hookOrder: null, error: null);

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
        var result = await CreateSut().HandleAsync(RequestAt(FeatureUri, 0, 0), CancellationToken.None);

        result.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_invalid_registry_returns_empty()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);

        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_no_hook_at_position_returns_empty()
    {
        var hook = MakeHook(HookType.BeforeScenario, csLine: 10, csColumn: 5);
        _registryLookup.GetRegistryForUri(CsUri)
            .Returns(ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook }));

        // Request position (0-based line 4, char 0) doesn't match the hook's location
        // (1-based line 10, col 5 -> 0-based line 9, char 4).
        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().BeEmpty();
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_hook_at_exact_position_returns_matching_scenarios()
    {
        // Hook at 1-based line 5, col 1 -> 0-based line 4, char 0.
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().ContainSingle();
        result.Scenarios[0].ScenarioName.Should().Be("S");
        result.Scenarios[0].Uri.Should().Be(FeatureUri.ToString());
    }

    [Fact]
    public async Task Handle_hook_scoped_to_a_tag_only_returns_scenarios_with_that_tag()
    {
        var hook = new ProjectHookBinding(
            new ProjectBindingImplementation("MyHook", null, new SourceLocation("/workspace/Hooks.cs", 5, 1)),
            scope: new BindingScope { Tag = ReqnrollTagExpressionParser.CreateTagLiteral("@foo") },
            HookType.BeforeScenario, hookOrder: null, error: null);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        const string feature = "Feature: F\n@foo\nScenario: Tagged\n    Given a step\nScenario: Untagged\n    Given a step\n";
        var matchSet = BuildMatchSet(feature, registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().ContainSingle();
        result.Scenarios[0].ScenarioName.Should().Be("Tagged");
    }

    [Fact]
    public async Task Handle_test_run_scoped_hook_returns_empty_regardless_of_scenarios()
    {
        var hook = MakeHook(HookType.BeforeTestRun);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_scenario_outline_is_marked_as_outline()
    {
        var hook = MakeHook(HookType.BeforeScenario);
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        const string feature = "Feature: F\nScenario Outline: SO\n    Given <x>\nExamples:\n    | x |\n    | 1 |\n";
        var matchSet = BuildMatchSet(feature, registry, FeatureUri.ToString());
        _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

        var result = await CreateSut().HandleAsync(RequestAt(CsUri, 4, 0), CancellationToken.None);

        result.Scenarios.Should().ContainSingle();
        result.Scenarios[0].IsOutline.Should().BeTrue();
    }
}
