using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Publishes <c>textDocument/publishDiagnostics</c> for a single <c>.cs</c> file's binding
/// validation errors (issue #514), reading the current binding registry via
/// <see cref="Registry.IProjectBindingRegistryLookup"/>. Shared by two triggers that need to stay
/// independent — see <see cref="CSharpDiagnosticsRegistryChangedHandler"/>'s remarks for why
/// neither alone is sufficient:
/// <list type="bullet">
///   <item><see cref="Features.TextSync.TextDocumentSyncHandler"/>, directly after every
///   <c>.cs</c> <c>didOpen</c>/<c>didChange</c> — unconditional, so a validity-only edit (e.g.
///   removing <c>static</c>) is reflected even when it doesn't change a step's matched expression
///   or a hook's scope/order, the two things that gate whether
///   <c>ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync</c> raises
///   <see cref="BindingRegistryChangedNotification"/> at all.</item>
///   <item><see cref="CSharpDiagnosticsRegistryChangedHandler"/>, for every
///   <see cref="BindingRegistryChangedNotification"/> — catches a registry change that didn't
///   originate from this file's own doc-sync events (e.g. the connector's startup reconciliation
///   racing this file's own <c>didOpen</c> — issue #514 spike finding, confirmed live: the
///   registry can be updated up to ~30s before any diagnostic would otherwise appear).</item>
/// </list>
/// </summary>
public interface ICSharpDiagnosticsPublisher
{
    /// <summary>
    /// Re-aggregates and pushes the complete current diagnostic set for <paramref name="uri"/>.
    /// A no-op when the file's owning project has no ready registry yet, or when the URI has no
    /// resolvable file-system path.
    /// </summary>
    void Publish(DocumentUri uri, int? version);
}
