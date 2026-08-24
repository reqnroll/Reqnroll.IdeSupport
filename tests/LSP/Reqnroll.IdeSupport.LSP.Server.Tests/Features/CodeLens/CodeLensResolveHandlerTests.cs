using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class CodeLensResolveHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    [Fact]
    public async Task ResolveAsync_routes_stepUsage_kind_to_StepCodeLensHandler()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var stepHandler = new StepCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var hookHandler = new HookMatchCountCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        _registryLookup.GetRegistryForUri(uri).Returns(ProjectBindingRegistry.Invalid);

        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(0, 0), new Position(0, 0)),
            Data = new JObject
            {
                ["kind"] = "stepUsage", ["uri"] = uri.ToString(),
                ["sourceFile"] = uri.GetFileSystemPath(), ["sourceLine"] = 5, ["sourceColumn"] = 1,
            }
        };

        var sut = new CodeLensResolveHandler(stepHandler, hookHandler);
        var result = await sut.ResolveAsync(lens, CancellationToken.None);

        result.Command!.Title.Should().Be("0 step usages"); // registry Invalid -> fallback path, but proves stepHandler (not hookHandler) ran
    }

    [Fact]
    public async Task ResolveAsync_unknown_kind_returns_the_lens_unchanged()
    {
        var stepHandler = new StepCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var hookHandler = new HookMatchCountCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(0, 0), new Position(0, 0)),
            Data = new JObject { ["kind"] = "somethingElse" }
        };

        var sut = new CodeLensResolveHandler(stepHandler, hookHandler);
        var result = await sut.ResolveAsync(lens, CancellationToken.None);

        result.Should().BeSameAs(lens);
    }
}
