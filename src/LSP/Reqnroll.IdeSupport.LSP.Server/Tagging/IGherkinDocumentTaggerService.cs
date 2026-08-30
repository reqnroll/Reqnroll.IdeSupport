using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tagging
{
    /// <summary>Parses the Gherkin tags for open and closed feature documents and keeps their binding match sets up to date.</summary>
    public interface IGherkinDocumentTaggerService
    {
        /// <summary>Parses the open document's current buffer into tags, matching it against its project's binding registry and storing the resulting match set.</summary>
        /// <param name="version">The expected document version; if it does not match the buffer's current version, parsing is skipped and an empty collection is returned.</param>
        Task<IReadOnlyCollection<IdeSupportTag>> ParseAsync(DocumentUri uri, int? version);

        /// <summary>
        /// Parses <paramref name="text"/> as the content of <paramref name="uri"/> using
        /// <paramref name="project"/>'s binding registry and stores the resulting match set
        /// keyed by <c>(uri, project)</c>.  The document buffer is not updated; open-file
        /// semantics are unaffected.
        /// Used for workspace-wide scans of feature files that are not currently open
        /// (e.g. the initial scan triggered on startup by a full binding-registry replacement).
        /// </summary>
        Task ScanClosedFileAsync(DocumentUri uri, string text, LspReqnrollProject project);

        /// <summary>
        /// Re-scans <paramref name="uri"/> from disk as a closed file for every project that owns
        /// it, repopulating the binding match cache. Called when a feature file is closed so its
        /// usages stay discoverable (Find Usages / Rename) after the open buffer is removed.
        /// No-op when the file is missing on disk or has no owning project.
        /// </summary>
        Task RescanClosedFileAsync(DocumentUri uri);
    }
}
