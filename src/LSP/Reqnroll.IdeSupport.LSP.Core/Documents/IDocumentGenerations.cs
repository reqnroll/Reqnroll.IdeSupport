namespace Reqnroll.IdeSupport.LSP.Core.Documents;

/// <summary>
/// A per-document counter of what the server believes the document's text is. It goes up on every
/// event that changes that belief: the editor opening, changing or closing the document (a close
/// switches the source of truth from the editor buffer to the file on disk), and a rename editing a
/// closed document.
/// </summary>
/// <remarks>
/// Unlike the client's document version, the generation exists for closed documents, never
/// decreases, and survives the document being closed and reopened. That lets state derived from a
/// document record which text it was built from, so a result computed before the text changed can
/// be told apart from one computed after.
/// </remarks>
public interface IDocumentGenerations
{
    /// <summary>Returns the document's current generation, or 0 if nothing has bumped it yet.</summary>
    /// <param name="documentId">The document URI as a string, compared case-insensitively.</param>
    long Current(string documentId);

    /// <summary>Advances the document's generation by one and returns the new value.</summary>
    /// <param name="documentId">The document URI as a string, compared case-insensitively.</param>
    long Bump(string documentId);
}
