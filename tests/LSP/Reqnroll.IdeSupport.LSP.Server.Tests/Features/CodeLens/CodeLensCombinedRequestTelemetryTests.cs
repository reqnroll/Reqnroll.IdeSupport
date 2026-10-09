using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

/// <summary>
/// Regression guard for issue #941: a single client <c>textDocument/codeLens</c> request is
/// dispatched by the combined registration in <c>LanguageServerOptionsExtensions</c> to every
/// lens handler at once — <see cref="StepCodeLensHandler"/> (.cs step usages),
/// <see cref="HookCodeLensHandler"/> (.feature hook matches) and
/// <see cref="HookMatchCountCodeLensHandler"/> (.cs hook-binding match counts) — and their lens
/// arrays are concatenated into one response. <c>FeatureUsageCatalog</c> maps
/// <c>textDocument/codeLens</c> as an exact-count Passive key, so that one request must be
/// measured exactly once, not once per handler.
/// </summary>
public class CodeLensCombinedRequestTelemetryTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IDocumentBufferService        _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();
    private readonly ClientIdeContext              _clientIde      = new("visualstudio");

    public CodeLensCombinedRequestTelemetryTests()
    {
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
    }

    /// <summary>
    /// One shared <see cref="IOperationDurationRecorder"/> is injected into every lens handler, so
    /// a duplicate measurement in ANY handler is observable here — this is what makes the guard
    /// cover the whole combined request rather than a single handler.
    /// </summary>
    [Theory]
    [InlineData("/workspace/test.feature")]
    [InlineData("/workspace/Steps.cs")]
    public async Task A_single_combined_codeLens_request_is_measured_exactly_once(string path)
    {
        var recorder = Substitute.For<IOperationDurationRecorder>();
        var uri      = DocumentUri.FromFileSystemPath(path);

        var step      = new StepCodeLensHandler(_matchService, _registryLookup, _clientIde, _logger, recorder);
        var hook      = new HookCodeLensHandler(_bufferService, _registryLookup, _logger, recorder);
        var hookMatch = new HookMatchCountCodeLensHandler(
            _matchService, _scopeManager, _registryLookup, _clientIde, _logger, recorder);

        // Mirrors the combined OnRequest registration: one client request is fanned out to every
        // lens handler, exactly as the real server does.
        var request = new CodeLensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } };
        await Task.WhenAll(
            step.HandleAsync(request, CancellationToken.None),
            hook.HandleAsync(request, CancellationToken.None),
            hookMatch.HandleAsync(request, CancellationToken.None));

        recorder.Received(1).Measure(
            LspStandardMethodNames.TextDocumentCodeLens, Arg.Any<DocumentUri?>(), Arg.Any<string?>());
    }
}
