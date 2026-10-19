using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Publishes <c>textDocument/publishDiagnostics</c> for a single <c>.cs</c> file's binding
/// validation errors, reading the current binding registry via
/// <see cref="Registry.IProjectBindingRegistryLookup"/>. The sole caller is
/// <see cref="CSharpDiagnosticsRegistryChangedHandler"/>, on every registry-change event
/// (<see cref="BindingRegistryReplacedNotification"/>,
/// <see cref="BindingRegistryPatchedNotification"/>, or
/// <see cref="ProjectBindingFilesRemovedNotification"/>) — which fires for both a live doc-sync edit
/// (<c>ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync</c>'s notify-gate,
/// <see cref="Bindings.ProjectBindingRegistry.HasExpressionChanges"/>/<c>HasHookChanges</c>,
/// includes <c>Error</c> so a validity-only edit like removing <c>static</c> is covered even
/// though it changes neither a step's matched expression nor a hook's scope/order) and a registry
/// change that didn't originate from this file's own doc-sync events at all (e.g. the connector's
/// startup reconciliation racing this file's own <c>didOpen</c> — confirmed live: the registry can be updated up to ~30s before any diagnostic would otherwise
/// appear). One trigger point, not two, is enough as a result: <c>TextDocumentSyncHandler</c>'s
/// doc-sync handlers do not call this service directly, because the notify-gate itself
/// considers <c>Error</c>.
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
