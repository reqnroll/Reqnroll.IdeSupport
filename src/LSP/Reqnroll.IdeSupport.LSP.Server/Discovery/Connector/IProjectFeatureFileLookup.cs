using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// Answers whether a project owns any feature files, from a source that knows real project
/// membership rather than the project folder's contents.
/// </summary>
/// <remarks>
/// Exists because <see cref="IProjectScope.GetFeatureFileCount"/> is implemented on
/// <c>LspReqnrollProject</c> as a recursive walk of the project folder, which misses a feature
/// file that is <em>linked</em> into the project from outside that folder. The server's
/// membership index, populated from the client's <c>reqnroll/projectFiles</c> baseline, is the
/// link-aware equivalent of the project-item enumeration the legacy VS extension counted.
/// </remarks>
public interface IProjectFeatureFileLookup
{
    /// <summary>
    /// Returns whether <paramref name="scope"/> owns at least one feature file, or
    /// <see langword="null"/> when that is not yet known — typically because the project's
    /// membership baseline has not arrived. Callers must treat <see langword="null"/> as
    /// "unknown", never as "no".
    /// </summary>
    bool? HasFeatureFiles(IProjectScope scope);
}
