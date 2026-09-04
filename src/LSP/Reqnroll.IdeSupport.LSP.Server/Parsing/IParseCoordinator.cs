using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Parsing;

/// <summary>
/// Lets a <c>[Serial]</c>-tagged LSP notification handler (<c>didOpen</c>/<c>didChange</c>) hand
/// its own reparse work off to a per-URI background chain instead of awaiting it inline, while
/// giving pull-based handlers with no server-initiated refresh capability a way to wait for that
/// work to land before reading state derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists (issue #471): OmniSharp's dispatcher funnels every <c>[Serial]</c>-tagged
/// notification (<c>didOpen</c>/<c>didChange</c>/<c>didSave</c>) through one shared global FIFO
/// lane for the entire server, and per its batch-barrier design an in-flight <c>[Serial]</c> item
/// also blocks the *start* of newly-arriving <c>[Parallel]</c> requests. Awaiting a full
/// Gherkin/Roslyn parse inline inside a sync handler occupies that lane for the parse's whole
/// duration. But simply not awaiting it (a raw fire-and-forget) would be a data-integrity
/// regression, not just a missed optimization: <c>textDocument/foldingRange</c> and
/// <c>textDocument/documentSymbol</c> have no LSP refresh capability, so a pull request racing an
/// in-flight background parse would get a silently wrong (empty/stale) answer with no
/// server-initiated way to correct it later — unlike <c>codeLens</c>/<c>semanticTokens</c>/
/// <c>inlayHint</c>, which all self-heal via their <c>workspace/*/refresh</c> requests.
/// </para>
/// <para>
/// This service is the replacement for that accidental correctness guarantee: <see cref="Schedule"/>
/// lets the sync handler return immediately (freeing the Serial lane for the next queued item),
/// and <see cref="WaitForReadyAsync"/> lets <c>FoldingRangeHandler</c>/<c>DocumentSymbolHandler</c>
/// (and <c>BindingRegistryChangedHandler</c>'s own <c>.cs</c>-driven feature-file reparse cascade —
/// see its remarks) observe the same pending work instead of reading state out from under it.
/// </para>
/// </remarks>
public interface IParseCoordinator
{
    /// <summary>
    /// Schedules <paramref name="work"/> to run for <paramref name="uri"/>, chained after any
    /// already-pending work for the same URI so two scheduled operations for one file never run
    /// concurrently with each other (avoiding races on shared per-document state such as
    /// <c>DocumentBuffer.Tags</c>). Returns immediately without waiting for <paramref name="work"/>
    /// to complete.
    /// </summary>
    void Schedule(DocumentUri uri, Func<CancellationToken, Task> work);

    /// <summary>
    /// Awaits the most recently scheduled work for <paramref name="uri"/>, if any is still
    /// pending; returns a completed task immediately if there is none (nothing was ever scheduled
    /// for this URI, or the last scheduled work has already finished).
    /// </summary>
    Task WaitForReadyAsync(DocumentUri uri, CancellationToken cancellationToken);
}
