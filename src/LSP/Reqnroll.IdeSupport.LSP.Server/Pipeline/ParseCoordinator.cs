using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="IParseCoordinator"/>
public sealed class ParseCoordinator : IParseCoordinator
{
    private readonly IIdeSupportLogger _logger;

    // Keyed by uri.ToString() (case-insensitive), matching IDocumentBufferService's own keying
    // convention rather than DocumentUri directly.
    private readonly ConcurrentDictionary<string, Task> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ParseCoordinator"/> class.</summary>
    public ParseCoordinator(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Schedule(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        var key = uri.ToString();
        Task? scheduled = null;

        // Chain onto any already-pending work for this URI so two scheduled operations for the
        // same file never run concurrently -- mirrors RefreshDebouncer's per-key coordination,
        // but chains (runs after) instead of cancels-and-replaces, since every scheduled parse
        // still needs to happen (unlike a debounced refresh, where only the last trigger matters).
        scheduled = _pending.AddOrUpdate(
            key,
            _ => RunSafelyAsync(uri, work),
            (_, previous) => previous.ContinueWith(
                _ => RunSafelyAsync(uri, work),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap());

        // Remove our own entry once it completes, but only if a newer Schedule call for this URI
        // hasn't already replaced it in the meantime -- same TryRemove(key, expectedValue) race
        // guard used by ConnectorBindingRegistryProvider/RefreshDebouncer elsewhere in this codebase.
        _ = scheduled.ContinueWith(
            t => _pending.TryRemove(new KeyValuePair<string, Task>(key, t)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc/>
    public Task WaitForReadyAsync(DocumentUri uri, CancellationToken cancellationToken)
        => _pending.TryGetValue(uri.ToString(), out var pending)
            ? pending.WaitAsync(cancellationToken)
            : Task.CompletedTask;

    private async Task RunSafelyAsync(DocumentUri uri, Func<CancellationToken, Task> work)
    {
        try
        {
            // CancellationToken.None: this work now runs detached from whatever request
            // triggered it (that request, a Serial-lane notification, has already returned).
            // A parse error here must not fault the tracked Task -- WaitForReadyAsync callers
            // (FoldingRangeHandler, DocumentSymbolHandler) would otherwise have that exception
            // rethrown into their own request handling for a completely unrelated failure.
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[ParseCoordinator] Scheduled work for '{uri}' failed: {ex.Message}");
        }
    }
}
