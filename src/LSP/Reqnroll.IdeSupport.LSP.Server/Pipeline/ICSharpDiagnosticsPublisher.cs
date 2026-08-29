using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Publishes <c>textDocument/publishDiagnostics</c> for a single <c>.cs</c> file's binding
/// validation errors (issue #514), reading the current binding registry via
/// <see cref="Registry.IProjectBindingRegistryLookup"/>. The sole caller is
/// <see cref="CSharpDiagnosticsRegistryChangedHandler"/>, on every
/// <see cref="BindingRegistryChangedNotification"/> — which fires for both a live doc-sync edit
/// (<c>ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync</c>'s notify-gate,
/// <see cref="Bindings.ProjectBindingRegistry.HasExpressionChanges"/>/<c>HasHookChanges</c>,
/// includes <c>Error</c> so a validity-only edit like removing <c>static</c> is covered even
/// though it changes neither a step's matched expression nor a hook's scope/order) and a registry
/// change that didn't originate from this file's own doc-sync events at all (e.g. the connector's
/// startup reconciliation racing this file's own <c>didOpen</c> — confirmed live in the issue
/// #514 spike: the registry can be updated up to ~30s before any diagnostic would otherwise
/// appear). One trigger point, not two, is enough as a result — an earlier version of this
/// service was also called directly from <c>TextDocumentSyncHandler</c>'s doc-sync handlers to
/// work around the notify-gate not considering <c>Error</c>; now that the gate itself does, that
/// direct call was removed as redundant.
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
