using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Remembers, per document, a signature of the diagnostic set last pushed to the client so an
/// unchanged set is not pushed again.
/// </summary>
/// <remarks>
/// A <c>textDocument/publishDiagnostics</c> push replaces whatever the client held for the URI and
/// nothing requires re-sending an identical set, so skipping it is lossless as long as the entry
/// never claims more than the client holds: it is reset on <c>didOpen</c>, evicted on
/// <c>didClose</c> and cleared on (re-)initialisation.
/// </remarks>
public interface IPublishedDiagnosticsCache
{
    /// <summary>
    /// Sends <paramref name="publish"/> unless <paramref name="diagnostics"/> equals the set last
    /// sent for <paramref name="uri"/>, and records it as sent. The check, the send and the record
    /// happen atomically per URI, so concurrent callers are sent in the order they are recorded.
    /// </summary>
    /// <param name="uri">The document the diagnostics belong to.</param>
    /// <param name="diagnostics">The complete diagnostic set that <paramref name="publish"/> would send.</param>
    /// <param name="isOpen">
    /// Evaluated under the same locks <see cref="Evict"/> takes. When it returns <c>false</c> nothing
    /// is sent or recorded, so a publish racing a close cannot resurrect the evicted entry.
    /// </param>
    /// <param name="publish">Sends the notification; invoked at most once.</param>
    /// <returns><c>true</c> if <paramref name="publish"/> was invoked.</returns>
    bool TryPublish(DocumentUri uri, IReadOnlyCollection<Diagnostic> diagnostics, Func<bool> isOpen, Action publish);

    /// <summary>Forgets the last-sent set for <paramref name="uri"/> so the next publish is always sent (document opened).</summary>
    void Reset(DocumentUri uri);

    /// <summary>Removes the entry for <paramref name="uri"/> (document closed).</summary>
    void Evict(DocumentUri uri);

    /// <summary>Forgets every entry (the client session was (re-)initialised).</summary>
    void Clear();
}
