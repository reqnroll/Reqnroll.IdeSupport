using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;


using Reqnroll.IdeSupport.LSP.Core.Matching;


using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

public class GherkinDocumentTaggerServiceTests
{
    private readonly IDocumentBufferService        _bufferService       = Substitute.For<IDocumentBufferService>();
    private readonly IIdeSupportTagParser            _tagParser           = Substitute.For<IIdeSupportTagParser>();
    private readonly IProjectBindingRegistryLookup _registryLookup      = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ISemanticTokensService         _semanticTokenService = Substitute.For<ISemanticTokensService>();
    private readonly IBindingMatchService          _bindingMatchService  = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager         = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger               _logger               = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE             _fileSystem           = new FileSystemForIDE();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public GherkinDocumentTaggerServiceTests()
    {
        // Default: no project registered for this URI, so Invalid is returned.
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.Invalid);
        // Default: no primary owner resolved.
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);
    }

    private GherkinDocumentTaggerService CreateSut() =>
        new(_bufferService, _tagParser, _registryLookup, _semanticTokenService,
            _bindingMatchService, _scopeManager, _logger, _fileSystem);

    private static LspReqnrollProject MakeProject(string folder = "/workspace")
    {
        var ideScope = new LspIdeScope(Substitute.For<IIdeSupportLogger>());
        return new LspReqnrollProject(
            new Reqnroll.IdeSupport.LSP.Server.Workspace.ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = folder,
                ProjectFile            = folder + "/My.csproj",
                ProjectFolder          = folder,
                OutputAssemblyPath     = folder + "/bin/My.dll",
                TargetFrameworkMoniker = "net8.0"
            },
            ideScope);
    }

    // ── Buffer-not-found ──────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_returns_empty_when_buffer_not_registered()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(x =>
        {
            x[1] = (DocumentBuffer?)null;
            return false;
        });

        var sut = CreateSut();
        var result = await sut.ParseAsync(FeatureUri, version: 1);
        result.Should().BeEmpty();
    }

    // ── Version mismatch ──────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_returns_empty_when_version_does_not_match()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: X\n");
        DocumentBuffer? ignored2;
        _bufferService.TryGet(FeatureUri, out ignored2).Returns(x =>
        {
            x[1] = buf;
            return true;
        });

        var sut = CreateSut();
        var result = await sut.ParseAsync(FeatureUri, version: 99);
        result.Should().BeEmpty();
    }

    // ── Successful parse ──────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAsync_invokes_tag_parser_and_returns_tags()
    {
        var buf = new DocumentBuffer(FeatureUri, 5, "Feature: X\n");
        DocumentBuffer? ignored4;
        _bufferService.TryGet(FeatureUri, out ignored4).Returns(x =>
        {
            x[1] = buf;
            return true;
        });

        var expectedTags = (IReadOnlyCollection<IdeSupportTag>)Array.Empty<IdeSupportTag>();
        _tagParser.Parse(
                      Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                      Arg.Any<ProjectBindingRegistry>())
                  .Returns(expectedTags);

        var sut = CreateSut();
        var result = await sut.ParseAsync(FeatureUri, version: 5);
        result.Should().BeSameAs(expectedTags);
        _tagParser.Received(1).Parse(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<ProjectBindingRegistry>());
        _bindingMatchService.Received(1).Store(
            Arg.Is<FeatureBindingMatchSet>(s => s.DocumentId == FeatureUri.ToString()));
        _semanticTokenService.Received(1).InvalidateCache(FeatureUri);
    }

    [Fact]
    public async Task ParseAsync_stores_match_set_with_primary_owner_key()
    {
        var project = MakeProject();
        _scopeManager.ResolvePrimaryOwner(FeatureUri).Returns(project);

        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: X\n");
        DocumentBuffer? _;
        _bufferService.TryGet(FeatureUri, out _).Returns(x => { x[1] = buf; return true; });
        _tagParser.Parse(Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                         Arg.Any<ProjectBindingRegistry>())
                  .Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ParseAsync(FeatureUri, version: 1);

        _bindingMatchService.Received(1).Store(
            Arg.Is<FeatureBindingMatchSet>(s =>
                s.DocumentId == FeatureUri.ToString() &&
                s.Owner.ProjectFile == project.ProjectFullName &&
                s.Owner.Tfm        == project.TargetFrameworkMoniker));
    }

    [Fact]
    public async Task ParseAsync_stores_with_unknown_owner_when_no_primary_owner_resolved()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: X\n");
        DocumentBuffer? _;
        _bufferService.TryGet(FeatureUri, out _).Returns(x => { x[1] = buf; return true; });
        _tagParser.Parse(Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                         Arg.Any<ProjectBindingRegistry>())
                  .Returns(Array.Empty<IdeSupportTag>());

        await CreateSut().ParseAsync(FeatureUri, version: 1);

        _bindingMatchService.Received(1).Store(
            Arg.Is<FeatureBindingMatchSet>(s =>
                s.DocumentId == FeatureUri.ToString() &&
                !s.Owner.IsKnown));
    }

    [Fact]
    public async Task ParseAsync_passes_when_no_version_specified()
    {
        var buf = new DocumentBuffer(FeatureUri, 5, "Feature: X\n");
        DocumentBuffer? ignored5;
        _bufferService.TryGet(FeatureUri, out ignored5).Returns(x =>
        {
            x[1] = buf;
            return true;
        });

        var expectedTags = (IReadOnlyCollection<IdeSupportTag>)Array.Empty<IdeSupportTag>();
        _tagParser.Parse(
                      Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                      Arg.Any<ProjectBindingRegistry>())
                  .Returns(expectedTags);

        var sut = CreateSut();
        var result = await sut.ParseAsync(FeatureUri, version: null);
        result.Should().BeSameAs(expectedTags);
    }

    // ── ScanClosedFileAsync open-document guard (Anomaly A) ────────────────────

    [Fact]
    public async Task ScanClosedFileAsync_skips_when_document_is_open()
    {
        var project = MakeProject();

        var buf = new DocumentBuffer(FeatureUri, 2, "Feature: Open\n");
        DocumentBuffer? open;
        _bufferService.TryGet(FeatureUri, out open).Returns(x =>
        {
            x[1] = buf;
            return true;
        });

        await CreateSut().ScanClosedFileAsync(FeatureUri, "Feature: Open\n", project);

        _tagParser.DidNotReceive().Parse(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<ProjectBindingRegistry>());
        _bindingMatchService.DidNotReceive().Store(Arg.Any<FeatureBindingMatchSet>());
    }

    [Fact]
    public async Task ScanClosedFileAsync_stores_match_set_when_document_is_not_open()
    {
        var project = MakeProject();

        DocumentBuffer? closed;
        _bufferService.TryGet(FeatureUri, out closed).Returns(x =>
        {
            x[1] = (DocumentBuffer?)null;
            return false;
        });

        var expectedTags = (IReadOnlyCollection<IdeSupportTag>)Array.Empty<IdeSupportTag>();
        _tagParser.Parse(
                      Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                      Arg.Any<ProjectBindingRegistry>())
                  .Returns(expectedTags);

        await CreateSut().ScanClosedFileAsync(FeatureUri, "Feature: Closed\n", project);

        _bindingMatchService.Received(1).Store(
            Arg.Is<FeatureBindingMatchSet>(s =>
                s.DocumentId == FeatureUri.ToString() &&
                s.Owner.ProjectFile == project.ProjectFullName));
    }

    // ── RescanClosedFileAsync (close → repopulate match cache from disk) ───────

    [Fact]
    public async Task RescanClosedFileAsync_reads_disk_and_stores_match_set_for_each_owner()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "reqnroll-rescan-" + Guid.NewGuid().ToString("N")));
        try
        {
            var filePath = Path.Combine(dir.FullName, "test.feature");
            await File.WriteAllTextAsync(filePath, "Feature: X\n");
            var uri = DocumentUri.FromFileSystemPath(filePath);

            var project = MakeProject(dir.FullName);
            _scopeManager.ResolveOwners(uri).Returns(new[] { project });

            // The buffer was already removed (close path) → closed-file scan proceeds.
            DocumentBuffer? none;
            _bufferService.TryGet(uri, out none).Returns(x => { x[1] = (DocumentBuffer?)null; return false; });
            _tagParser.Parse(Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
                             Arg.Any<ProjectBindingRegistry>())
                      .Returns(Array.Empty<IdeSupportTag>());

            await CreateSut().RescanClosedFileAsync(uri);

            _bindingMatchService.Received(1).Store(
                Arg.Is<FeatureBindingMatchSet>(s =>
                    s.DocumentId == uri.ToString() &&
                    s.Owner.ProjectFile == project.ProjectFullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RescanClosedFileAsync_is_noop_when_file_missing_on_disk()
    {
        var uri = DocumentUri.FromFileSystemPath(
            Path.Combine(Path.GetTempPath(), "reqnroll-missing-" + Guid.NewGuid().ToString("N") + ".feature"));
        _scopeManager.ResolveOwners(uri).Returns(new[] { MakeProject() });

        await CreateSut().RescanClosedFileAsync(uri);

        _bindingMatchService.DidNotReceive().Store(Arg.Any<FeatureBindingMatchSet>());
    }

    [Fact]
    public async Task RescanClosedFileAsync_is_noop_when_no_owning_project()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "reqnroll-rescan-" + Guid.NewGuid().ToString("N")));
        try
        {
            var filePath = Path.Combine(dir.FullName, "test.feature");
            await File.WriteAllTextAsync(filePath, "Feature: X\n");
            var uri = DocumentUri.FromFileSystemPath(filePath);

            _scopeManager.ResolveOwners(uri).Returns(Array.Empty<LspReqnrollProject>());

            await CreateSut().RescanClosedFileAsync(uri);

            _bindingMatchService.DidNotReceive().Store(Arg.Any<FeatureBindingMatchSet>());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
