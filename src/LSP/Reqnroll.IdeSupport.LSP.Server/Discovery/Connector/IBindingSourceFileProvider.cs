using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Supplies the <c>.cs</c> source files of a project that may declare bindings, each paired with
/// the text to parse, so <see cref="ConnectorBindingRegistryProvider"/> can discover bindings
/// from source when the out-of-process connector cannot provide them.
/// </summary>
public interface IBindingSourceFileProvider
{
    /// <summary>
    /// Returns every <c>.cs</c> file of <paramref name="project"/> that may declare bindings: open
    /// buffers with their current (possibly unsaved) text, and the remaining project files with
    /// their on-disk text.
    /// </summary>
    IReadOnlyList<CsFileToReconcile> GetBindingSourceFiles(LspReqnrollProject project);
}
