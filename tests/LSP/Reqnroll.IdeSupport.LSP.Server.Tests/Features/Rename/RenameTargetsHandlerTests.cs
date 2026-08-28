using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class RenameTargetsHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();
    private readonly IDocumentBufferService         _documentBuffer = Substitute.For<IDocumentBufferService>();
    private readonly ICSharpFileTextCache          _csharpFileTextCache = new CSharpFileTextCache();
    private readonly IFileSystemForIDE             _fileSystem = new FileSystemForIDE();

    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/A.feature");
    private static readonly DocumentUri TxtUri = DocumentUri.FromFileSystemPath("/workspace/readme.txt");

    private RenameTargetsHandler CreateSut() =>
        new(
            _registryLookup,
            new RenameBindingResolver(_matchService, _scopeManager, new RenameSessionManager(), _logger),
            new CSharpAttributeLiteralResolver(_csharpFileTextCache, _documentBuffer, _logger, _fileSystem));

    [Fact]
    public async Task Returns_null_for_a_uri_with_neither_a_cs_nor_feature_extension_Async()
    {
        var result = await CreateSut().HandleRenameTargetsAsync(
            new RenameTargetsParams { TextDocument = TxtUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_an_empty_response_for_a_cs_file_when_the_registry_is_Invalid_Async()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);

        var result = await CreateSut().HandleRenameTargetsAsync(
            new RenameTargetsParams { TextDocument = CsUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_an_empty_response_for_a_cs_file_with_no_bindings_at_the_cursor_Async()
    {
        var registry = new ProjectBindingRegistry(
            Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), projectHash: 0);
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var result = await CreateSut().HandleRenameTargetsAsync(
            new RenameTargetsParams { TextDocument = CsUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_an_empty_response_for_a_feature_file_with_no_owning_project_Async()
    {
        _scopeManager.ResolveOwners(FeatureUri).Returns(Array.Empty<LspReqnrollProject>());

        var result = await CreateSut().HandleRenameTargetsAsync(
            new RenameTargetsParams { TextDocument = FeatureUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Targets.Should().BeEmpty();
    }
}
