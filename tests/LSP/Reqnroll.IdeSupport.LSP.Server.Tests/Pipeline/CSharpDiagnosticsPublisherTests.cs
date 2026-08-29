using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class CSharpDiagnosticsPublisherTests
{
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ICSharpDiagnosticsAggregator _aggregator = Substitute.For<ICSharpDiagnosticsAggregator>();
    private readonly ILanguageServerFacade _facade = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private CSharpDiagnosticsPublisher CreateSut() => new(_registryLookup, _aggregator, _facade, _logger);

    [Fact]
    public void Does_not_push_when_registry_is_not_ready()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);

        CreateSut().Publish(CsUri, 1);

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishDiagnosticsParams>());
        _aggregator.DidNotReceiveWithAnyArgs().Aggregate(default!, default!);
    }

    [Fact]
    public void Pushes_the_aggregator_output_converted_to_lsp_diagnostics()
    {
        var registry = new ProjectBindingRegistry([], [], projectHash: 1);
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

        var location = new SourceLocation(CsUri.GetFileSystemPath()!, 10, 5, 10, 12);
        _aggregator.Aggregate(registry, Arg.Any<string>())
            .Returns([new CSharpBindingDiagnostic("must be static", location, GherkinDiagnosticSeverity.Error)]);

        CreateSut().Publish(CsUri, 3);

        _facade.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p =>
                p.Uri == CsUri &&
                p.Version == 3 &&
                p.Diagnostics.Count() == 1 &&
                p.Diagnostics.Single().Message == "must be static" &&
                p.Diagnostics.Single().Severity == DiagnosticSeverity.Error &&
                p.Diagnostics.Single().Source == CSharpDiagnosticsPublisher.Source &&
                p.Diagnostics.Single().Range.Start == new Position(9, 4) &&
                p.Diagnostics.Single().Range.End == new Position(9, 11)));
    }
}
