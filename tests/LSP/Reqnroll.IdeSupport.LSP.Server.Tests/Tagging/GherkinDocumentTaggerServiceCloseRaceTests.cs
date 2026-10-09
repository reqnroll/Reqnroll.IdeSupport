using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

/// <summary>
/// Issue #938: <see cref="GherkinDocumentTaggerService.ParseAsync"/> reads the open buffer, runs a
/// (potentially long) parse, then stores the resulting tags. The parse runs on the
/// <see cref="Parsing.IParseCoordinator"/> lane, while <c>didClose</c> removes the buffer inline,
/// so a close can land between the read and the store. These tests hold the parse open with a gate
/// (no sleeps) so the close deterministically lands inside that window.
/// </summary>
public class GherkinDocumentTaggerServiceCloseRaceTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private readonly IDocumentBufferService        _bufferService        = new DocumentBufferService();
    private readonly IIdeSupportTagParser            _tagParser            = Substitute.For<IIdeSupportTagParser>();
    private readonly IProjectBindingRegistryLookup _registryLookup       = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ISemanticTokensService         _semanticTokenService = Substitute.For<ISemanticTokensService>();
    private readonly IBindingMatchService          _bindingMatchService  = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager         = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger               _logger               = Substitute.For<IIdeSupportLogger>();

    // Signalled by the parser once ParseAsync has read the buffer and entered the parse.
    private readonly ManualResetEventSlim _parseStarted = new();
    // Released by the test once it has simulated didClose, letting the parse complete.
    private readonly ManualResetEventSlim _releaseParse = new();

    public GherkinDocumentTaggerServiceCloseRaceTests()
    {
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        // Only the open-document parse (non-zero version) is gated; the closed-file scan parses
        // a version-0 snapshot read from disk and must run straight through.
        _tagParser.Parse(Arg.Any<IGherkinTextSnapshot>(), Arg.Any<ProjectBindingRegistry>())
                  .Returns(call =>
                  {
                      if (call.Arg<IGherkinTextSnapshot>().Version != 0)
                      {
                          _parseStarted.Set();
                          _releaseParse.Wait(GateTimeout).Should().BeTrue("the test must release the gated parse");
                      }
                      return new List<IdeSupportTag>();
                  });
    }

    private GherkinDocumentTaggerService CreateSut() =>
        new(_bufferService, _tagParser, _registryLookup, _semanticTokenService,
            _bindingMatchService, _scopeManager, _logger, new FileSystemForIDE());

    /// <summary>Runs ParseAsync on another thread, simulates didClose while the parse is in flight, then lets the parse finish.</summary>
    private async Task ParseThenCloseMidParseAsync(GherkinDocumentTaggerService sut, DocumentUri uri, int version)
    {
        var parse = Task.Run(() => sut.ParseAsync(uri, version));

        _parseStarted.Wait(GateTimeout).Should().BeTrue("ParseAsync must reach the tag parser");
        _bufferService.Remove(uri); // what TextDocumentSyncHandler's didClose does
        _releaseParse.Set();

        await parse;
    }

    [Fact]
    public async Task ParseAsync_completing_after_close_does_not_recreate_the_buffer()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/race.feature");
        _bufferService.Update(uri, 3, "Feature: X\n");

        await ParseThenCloseMidParseAsync(CreateSut(), uri, version: 3);

        _bufferService.TryGet(uri, out var ghost).Should().BeFalse(
            $"a parse finishing after didClose must not resurrect a buffer (found: {ghost})");
        _bufferService.All.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_completing_after_close_does_not_store_a_match_set_for_the_closed_document()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/race.feature");
        _bufferService.Update(uri, 3, "Feature: X\n");

        await ParseThenCloseMidParseAsync(CreateSut(), uri, version: 3);

        _bindingMatchService.DidNotReceive().Store(Arg.Any<FeatureBindingMatchSet>());
        _semanticTokenService.DidNotReceive().InvalidateCache(Arg.Any<DocumentUri>());
    }

    [Fact]
    public async Task RescanClosedFileAsync_after_a_parse_that_completed_post_close_still_rescans_from_disk()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "reqnroll-938-" + Guid.NewGuid().ToString("N")));
        try
        {
            var filePath = Path.Combine(dir.FullName, "race.feature");
            await File.WriteAllTextAsync(filePath, "Feature: X\n");
            var uri = DocumentUri.FromFileSystemPath(filePath);

            var project = new LspReqnrollProject(
                new ReqnrollProjectLoadedParams
                {
                    WorkspaceFolder        = dir.FullName,
                    ProjectFile            = Path.Combine(dir.FullName, "My.csproj"),
                    ProjectFolder          = dir.FullName,
                    OutputAssemblyPath     = Path.Combine(dir.FullName, "bin", "My.dll"),
                    TargetFrameworkMoniker = "net8.0"
                },
                new LspIdeScope(Substitute.For<IIdeSupportLogger>()));
            _scopeManager.ResolveOwners(uri).Returns(new[] { project });

            var sut = CreateSut();
            _bufferService.Update(uri, 3, "Feature: X\n");

            // didClose: Remove lands mid-parse, then the close handler rescans from disk.
            await ParseThenCloseMidParseAsync(sut, uri, version: 3);
            await sut.RescanClosedFileAsync(uri);

            // A ghost buffer would make ScanClosedFileAsync treat the file as open and skip it,
            // leaving the closed file without a project-keyed match set.
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
}
