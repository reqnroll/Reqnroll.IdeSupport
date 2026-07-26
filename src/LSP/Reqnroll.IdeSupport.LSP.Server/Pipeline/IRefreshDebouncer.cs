namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Debounces a named action so a burst of rapid triggers collapses into a single run after they
/// settle, instead of running the action once per trigger.
/// </summary>
/// <remarks>
/// MediatR notification handlers are registered as transients (see the remarks on
/// <c>ServiceCollectionExtensions.AddReqnrollLspHandlers</c>): a new handler instance is
/// constructed for every notification, so debounce state kept in a handler's own instance field
/// (e.g. a <c>CancellationTokenSource</c>) never actually debounces anything — each instance only
/// ever sees the one notification that created it, with nothing to cancel. This service exists to
/// hold that state somewhere that outlives a single notification, injected as a singleton into the
/// handlers that need it. Mirrors <see cref="IFeatureRescanDebouncer"/>, generalised with a string
/// key so unrelated handlers (code lens refresh, semantic tokens refresh, inlay hint refresh, ...)
/// can share one implementation without their debounce windows colliding.
/// </remarks>
public interface IRefreshDebouncer
{
    /// <summary>
    /// Schedules <paramref name="action"/> to run after <paramref name="delay"/> elapses. A call
    /// with the same <paramref name="key"/> before the window elapses cancels the pending run and
    /// restarts the window, so only the last action scheduled for that key actually runs.
    /// </summary>
    void Schedule(string key, TimeSpan delay, Func<CancellationToken, Task> action);
}
