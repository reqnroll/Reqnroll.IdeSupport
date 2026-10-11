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
/// lens handler at once -- <see cref="StepCodeLensHandler"/> (.cs step usages),
/// <see cref="HookCodeLensHandler"/> (.feature hook matches) and
/// <see cref="HookMatchCountCodeLensHandler"/> (.cs hook-binding match counts). Each handler
/// measures only the work it actually does, i.e. past its own file-type guard: a handler that
/// does not serve the file must not record, while a .cs file legitimately yields one measurement
/// per lens kind that serves it (step usages and hook match counts).
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
    /// a measurement from a handler that does not serve the file is observable here.
    /// </summary>
    [Theory]
    [InlineData("/workspace/test.feature", 1)] // HookCodeLensHandler only
    [InlineData("/workspace/Steps.cs", 2)]     // StepCodeLensHandler + HookMatchCountCodeLensHandler
    [InlineData("/workspace/readme.txt", 0)]   // no handler serves it
    public async Task A_combined_codeLens_request_is_measured_once_per_handler_that_serves_the_file(
        string path, int expectedMeasurements)
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

        recorder.Received(expectedMeasurements).Measure(
            LspStandardMethodNames.TextDocumentCodeLens, Arg.Any<DocumentUri?>(), Arg.Any<string?>());
    }
}
