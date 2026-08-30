using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;

/// <summary>
/// Applies an immediate, source-level (Roslyn) binding update for a single <c>.cs</c> file,
/// patching the owning project's binding registry so step matches update as the user types —
/// before any build. See <see cref="CSharpBindingDiscoveryService"/> (Roslyn/C# source-level
/// binding discovery).
/// </summary>
public interface ICSharpBindingDiscoveryService
{
    /// <summary>
    /// Re-discovers the bindings declared in <paramref name="text"/> (the current contents of the
    /// <c>.cs</c> document at <paramref name="uri"/>) and replaces that file's entries in its
    /// project's binding registry. No-ops when the document has no owning project or the project
    /// has no binding provider yet.
    /// <param name="isOpen"><see langword="true"/> when triggered by a <c>textDocument/didOpen</c>
    /// event; <see langword="false"/> when triggered by <c>textDocument/didChange</c>. Used for
    /// telemetry to distinguish <c>csOpen</c> from <c>csEdit</c> trigger contexts.</param>
    /// </summary>
    Task UpdateFromSourceAsync(DocumentUri uri, string text, bool isOpen, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a Roslyn source-level binding update for a single <c>.cs</c> file, targeting
    /// a specific project directly — bypassing the membership-index owner resolution.
    /// Used by <see cref="BindingRegistryChangedHandler.RediscoverCsFilesAsync"/> (startup
    /// full-replacement reconciliation, when the baseline may not have arrived yet) and
    /// <see cref="BindingRegistryChangedHandler.RemoveBindingFilesAsync"/> (purging a
    /// project-files-delta-removed file's bindings).
    /// </summary>
    /// <param name="notify">
    /// Passed straight through to <see cref="Registry.ConnectorBindingRegistryProvider.ApplyRoslynFileUpdateAsync"/>
    /// — see its remarks. <see cref="BindingRegistryChangedHandler.RediscoverCsFilesAsync"/> passes
    /// <see langword="false"/> since its caller already reparses and notifies unconditionally right
    /// after it returns; every other caller keeps the default (issue #471).
    /// </param>
    Task UpdateFromSourceForProjectAsync(
        LspReqnrollProject project, string filePath, string text, CancellationToken cancellationToken,
        bool notify = true);

    /// <summary>
    /// Removes all bindings declared in the <c>.cs</c> file at <paramref name="uri"/> from every
    /// owning project's binding registry.  Called when the file is deleted on disk so the registry
    /// does not retain stale step-definition entries.
    /// </summary>
    Task RemoveFileAsync(DocumentUri uri, CancellationToken cancellationToken);
}
