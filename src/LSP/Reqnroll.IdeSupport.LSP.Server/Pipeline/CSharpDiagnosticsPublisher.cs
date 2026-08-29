using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="ICSharpDiagnosticsPublisher"/>
public sealed class CSharpDiagnosticsPublisher : ICSharpDiagnosticsPublisher
{
    /// <summary>LSP <c>source</c> field value for .cs binding-validation diagnostics.</summary>
    public const string Source = "reqnroll.binding";

    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly ICSharpDiagnosticsAggregator _aggregator;
    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="CSharpDiagnosticsPublisher"/> class.</summary>
    public CSharpDiagnosticsPublisher(
        IProjectBindingRegistryLookup registryLookup,
        ICSharpDiagnosticsAggregator aggregator,
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger)
    {
        _registryLookup = registryLookup;
        _aggregator = aggregator;
        _languageServer = languageServer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Publish(DocumentUri uri, int? version)
    {
        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return;

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (ReferenceEquals(registry, ProjectBindingRegistry.Invalid))
        {
            _logger.LogVerbose($"CSharpDiagnosticsPublisher: no registry yet for {uri} — skipping.");
            return;
        }

        var diagnostics = _aggregator.Aggregate(registry, filePath)
            .Select(ToLspDiagnostic)
            .ToArray();

        _logger.LogVerbose($"CSharpDiagnosticsPublisher: pushing {diagnostics.Length} diagnostic(s) for {uri}.");

        _languageServer.SendNotification(
            LspMethodNames.TextDocumentPublishDiagnostics,
            new PublishDiagnosticsParams
            {
                Uri = uri,
                Version = version,
                Diagnostics = new Container<Diagnostic>(diagnostics)
            });
    }

    private static Diagnostic ToLspDiagnostic(CSharpBindingDiagnostic d)
    {
        var loc = d.Location;

        // 1-based (discovery layer) -> 0-based (LSP), end-exclusive — matching the convention
        // established in SourceLocationExtensions.WithIdentifierLocation.
        var startLine = loc.SourceFileLine - 1;
        var startCol = loc.SourceFileColumn - 1;
        var endLine = (loc.SourceFileEndLine ?? loc.SourceFileLine) - 1;
        var endCol = (loc.SourceFileEndColumn ?? loc.SourceFileColumn) - 1;

        return new Diagnostic
        {
            Range = new LspRange(new Position(startLine, startCol), new Position(endLine, endCol)),
            Severity = d.Severity == GherkinDiagnosticSeverity.Error
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning,
            Source = Source,
            Message = d.Message
        };
    }
}
