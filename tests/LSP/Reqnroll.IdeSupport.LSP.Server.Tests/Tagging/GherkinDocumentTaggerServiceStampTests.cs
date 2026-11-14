using System.Collections.Immutable;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

/// <summary>
/// The tagger records a <see cref="DerivationStamp"/> on every match set it stores, and reads each
/// input's version before the input itself. The ordering tests bump a generation at the moment its
/// input is read (in the configuration provider, in the file read): the stamp must hold the value
/// from before that bump, never one newer than the input it describes.
/// </summary>
public class GherkinDocumentTaggerServiceStampTests
{
    private const string Text = "Feature: F\n\nScenario: S\n    Given a step\n";

    private readonly IIdeSupportLogger             _logger              = Substitute.For<IIdeSupportLogger>();
    private readonly IDocumentBufferService        _bufferService       = new DocumentBufferService();
    private readonly IProjectBindingRegistryLookup _registryLookup      = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ILspWorkspaceScopeManager     _scopeManager        = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly BindingMatchService           _matchService;
    private readonly IDocumentGenerations          _documentGenerations = new DocumentGenerations();
    private readonly IProjectConfigGenerations     _configGenerations   = new ProjectConfigGenerations();
    private readonly IIdeSupportConfigurationProvider _configProvider   = Substitute.For<IIdeSupportConfigurationProvider>();

    private readonly ProjectBindingRegistry _registry =
        new(ImmutableArray<ProjectStepDefinitionBinding>.Empty, ImmutableArray<ProjectHookBinding>.Empty, 0);

    private readonly LspReqnrollProject _project;
    private readonly ProjectOwner       _owner;
    private readonly DocumentUri        _uri = DocumentUri.FromFileSystemPath("/workspace/stamp.feature");

    public GherkinDocumentTaggerServiceStampTests()
    {
        _matchService = new BindingMatchService(_documentGenerations, _logger);
        _project = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = "/workspace",
                ProjectFile            = "/workspace/My.csproj",
                ProjectFolder          = "/workspace",
                OutputAssemblyPath     = "/workspace/bin/My.dll",
                TargetFrameworkMoniker = "net10.0"
            },
            new LspIdeScope(_logger));
        _owner = new ProjectOwner(_project.ProjectFullName, _project.TargetFrameworkMoniker);

        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
        _project.Properties[typeof(IIdeSupportConfigurationProvider)] = _configProvider;

        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(_registry);
        _scopeManager.ResolveOwners(_uri).Returns(new[] { _project });
    }

    private GherkinDocumentTaggerService CreateSut(IFileSystemForIDE? fileSystem = null) =>
        new(_bufferService, TaggerTestParser.Create(_logger), _registryLookup, Substitute.For<ISemanticTokensService>(),
            _matchService, _scopeManager, _logger, fileSystem ?? new FileSystemForIDE(), _documentGenerations, _configGenerations);

    private DerivationStamp StoredStamp(ProjectOwner owner)
    {
        _matchService.TryGet(new MatchSetKey(_uri.ToString(), owner), out var set).Should().BeTrue();
        return set.Stamp;
    }

    /// <summary>Makes reading the project's configuration (which yields the feature language) bump its config generation.</summary>
    private void BumpConfigGenerationWhenConfigurationIsRead()
        => _configProvider.GetConfiguration().Returns(_ =>
        {
            _configGenerations.Bump(_owner);
            return new IdeSupportConfiguration();
        });

    // ── ParseAsync (open documents) ──────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_stamps_the_buffer_generation_the_owners_config_generation_and_the_registry_version()
    {
        _scopeManager.ResolvePrimaryOwner(_uri).Returns(_project);
        _configGenerations.Bump(_owner);
        _configGenerations.Bump(_owner);
        _bufferService.Update(_uri, 1, Text, generation: 7);

        await CreateSut().ParseAsync(_uri, 1);

        StoredStamp(_owner).Should().Be(new DerivationStamp(7, 2, _registry.Version));
    }

    [Fact]
    public async Task ParseAsync_without_a_known_owner_stamps_config_generation_zero()
    {
        _scopeManager.ResolvePrimaryOwner(_uri).Returns((LspReqnrollProject?)null);
        _bufferService.Update(_uri, 1, Text, generation: 3);

        await CreateSut().ParseAsync(_uri, 1);

        StoredStamp(ProjectOwner.Unknown).Should().Be(new DerivationStamp(3, 0, _registry.Version));
    }

    [Fact]
    public async Task ParseAsync_reads_the_config_generation_before_the_feature_language()
    {
        _scopeManager.ResolvePrimaryOwner(_uri).Returns(_project);
        _bufferService.Update(_uri, 1, Text, generation: 1);
        BumpConfigGenerationWhenConfigurationIsRead();

        await CreateSut().ParseAsync(_uri, 1);

        StoredStamp(_owner).ConfigGeneration.Should().Be(0, "the stamp must not be newer than the configuration it was parsed with");
    }

    // ── Closed files ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanClosedFileAsync_stamps_the_given_content_generation_the_projects_config_generation_and_the_registry_version()
    {
        _configGenerations.Bump(_owner);

        await CreateSut().ScanClosedFileAsync(_uri, Text, _project, contentGeneration: 5);

        StoredStamp(_owner).Should().Be(new DerivationStamp(5, 1, _registry.Version));
    }

    [Fact]
    public async Task ScanClosedFileAsync_reads_the_config_generation_before_the_feature_language()
    {
        BumpConfigGenerationWhenConfigurationIsRead();

        await CreateSut().ScanClosedFileAsync(_uri, Text, _project, contentGeneration: 5);

        StoredStamp(_owner).ConfigGeneration.Should().Be(0);
    }

    [Fact]
    public async Task RescanClosedFileAsync_reads_the_content_generation_before_the_text()
    {
        var path = _uri.GetFileSystemPath();
        _documentGenerations.Bump(_uri.ToString()); // generation 1 when the rescan starts
        var fileSystem = Substitute.For<IFileSystemForIDE>();
        fileSystem.File.Exists(path).Returns(true);
        fileSystem.File.ReadAllTextAsync(path, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _documentGenerations.Bump(_uri.ToString()); // the text changes while it is read
            return Task.FromResult(Text);
        });

        await CreateSut(fileSystem).RescanClosedFileAsync(_uri);

        StoredStamp(_owner).ContentGeneration.Should().Be(1, "the stamp must not be newer than the text it was built from");
    }

    [Fact]
    public async Task ScanClosedFileForAllOwnersAsync_stamps_every_owner_with_the_given_content_generation()
    {
        var second = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = "/workspace",
                ProjectFile            = "/workspace/Other.csproj",
                ProjectFolder          = "/workspace",
                OutputAssemblyPath     = "/workspace/bin/Other.dll",
                TargetFrameworkMoniker = "net10.0"
            },
            new LspIdeScope(_logger));
        _scopeManager.ResolveOwners(_uri).Returns(new[] { _project, second });

        await CreateSut().ScanClosedFileForAllOwnersAsync(_uri, Text, contentGeneration: 4);

        StoredStamp(_owner).ContentGeneration.Should().Be(4);
        StoredStamp(new ProjectOwner(second.ProjectFullName, second.TargetFrameworkMoniker)).ContentGeneration.Should().Be(4);
    }
}
