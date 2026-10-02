#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Hands out one cancellation token per operation, where starting a new operation cancels the one
/// still in flight — so a second press of a command supersedes the first instead of both completing
/// and their results (navigations, windows) arriving in either order. Every token is also cancelled
/// by the <paramref name="lifetimeToken"/> supplied at construction (e.g. VS shutdown).
/// </summary>
/// <remarks>
/// Not thread-safe by design: <see cref="Begin"/> is called from a command handler's <c>Exec</c>,
/// which VS always invokes on the UI thread. <see cref="End"/> may be called from any thread; it only
/// clears the slot if the ending operation is still the current one.
/// </remarks>
internal sealed class SupersedingCancellation
{
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _current;

    public SupersedingCancellation(CancellationToken lifetimeToken) => _lifetimeToken = lifetimeToken;

    /// <summary>Cancels the operation in flight (if any) and returns the source for a new one.</summary>
    public CancellationTokenSource Begin()
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        var previous = Interlocked.Exchange(ref _current, next);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous operation finished and End() disposed it between the exchange above and
            // this Cancel() — it no longer needs cancelling. (Begin runs on the UI thread, End on
            // whichever thread the operation completed on.)
        }
        return next;
    }

    /// <summary>
    /// Ends an operation started by <see cref="Begin"/>: releases its source, and clears it as the
    /// current operation unless a newer one has already replaced it.
    /// </summary>
    public void End(CancellationTokenSource operation)
    {
        Interlocked.CompareExchange(ref _current, null, operation);
        operation.Dispose();
    }
}
