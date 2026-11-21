using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Parsing;

/// <summary>
/// Lets a <c>[Serial]</c>-tagged LSP notification handler (<c>didOpen</c>/<c>didChange</c>) hand
/// its own reparse work off to a per-URI background slot instead of awaiting it inline, while
/// giving pull-based handlers with no server-initiated refresh capability a way to wait for that
/// work to land before reading state derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: OmniSharp's dispatcher funnels every <c>[Serial]</c>-tagged
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
/// <para>
/// There is at most one piece of work <em>running</em> and at most one <em>waiting</em> per URI. Work scheduled
/// while something is running replaces whatever was waiting, so a burst of edits costs the parse that is already
/// running plus one more, not one parse per edit. That is correct because everything scheduled here recomputes the
/// state derived from a document <em>as it is when the work runs</em> (the open buffer's text, the project's
/// binding registry and configuration are read at that moment), so the newest request makes every older one
/// redundant; work that is not of that kind must not go through this service.
/// </para>
/// </remarks>
public interface IParseCoordinator
{
    /// <summary>
    /// Schedules <paramref name="work"/> to run for <paramref name="uri"/>. If nothing is running for the URI it
    /// starts at once, on the calling thread up to its first await. Otherwise it waits for the running work to
    /// finish, replacing any work that was already waiting (which then never runs), so two operations for one file
    /// never run concurrently (avoiding races on shared per-document state such as <c>DocumentBuffer.Tags</c>) and
    /// at most one is ever left waiting. Returns immediately without waiting for <paramref name="work"/> to complete.
    /// </summary>
    void Schedule(DocumentUri uri, Func<CancellationToken, Task> work);

    /// <summary>
    /// Awaits the most recently scheduled work for <paramref name="uri"/>, if any is still pending: completes once
    /// that work has run (work replaced before it ran is covered by the work that replaced it); returns a completed
    /// task immediately if there is none (nothing was ever scheduled for this URI, or the last scheduled work has
    /// already finished).
    /// </summary>
    Task WaitForReadyAsync(DocumentUri uri, CancellationToken cancellationToken);
}
