using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// <see cref="Mediator"/> whose notification fan-out isolates handler faults: every
/// <see cref="INotificationHandler{TNotification}"/> is invoked inside its own <c>try</c>/<c>catch</c>,
/// so one throwing handler no longer suppresses every handler after it.
/// </summary>
/// <remarks>
/// <para>
/// MediatR's stock <see cref="Mediator.PublishCore"/> is a sequential <c>foreach</c> that awaits each
/// handler in turn with no exception handling (issue #575). The first handler to throw therefore
/// aborts the whole fan-out, and which handlers are lost depends on the order the DI container
/// returns them — i.e. on assembly-scan order, not on any deliberate decision.
/// </para>
/// <para>
/// That mattered most for <see cref="MatchCacheChangedNotification"/>, whose five consumers include
/// two that can throw (<see cref="DiagnosticsPublishHandler"/> and
/// <see cref="SemanticTokensPushHandler"/>; the three refresh handlers are safe only because they
/// defer their real work into <see cref="IRefreshDebouncer"/>, which catches). A failure in the
/// first of those meant the user silently lost both diagnostics and semantic-token colouring for
/// that edit, with the exception surfacing as a <c>LogWarning</c> from
/// <see cref="ParseCoordinator"/> attributed to the <em>parse</em> rather than to the handler that
/// actually failed — a log line that actively misdirected diagnosis.
/// </para>
/// <para>
/// Note this deliberately changes only fault behaviour, not dispatch behaviour: handlers still run
/// sequentially, in the same order, on the same thread. Callers that background the publish
/// themselves (<c>BindingRegistryProviderRouter.OnProviderChanged</c>,
/// <c>MembershipIndex.HandleProjectFilesAsync</c>) still need to, for the reasons documented at
/// those call sites (issue #477).
/// </para>
/// </remarks>
public sealed class ResilientMediator : Mediator
{
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ResilientMediator"/> class.</summary>
    public ResilientMediator(ServiceFactory serviceFactory, IIdeSupportLogger logger)
        : base(serviceFactory)
    {
        _logger = logger;
    }

    /// <summary>
    /// Invokes each handler in turn, logging and swallowing any exception so that the remaining
    /// handlers still run.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is logged at verbose rather than error: a cancelled
    /// publish is a normal outcome (a superseding edit, a closing document), not a fault. It is
    /// still swallowed rather than rethrown, so cancelling one handler does not silently drop the
    /// others — the caller's own token check remains the place cancellation is honoured.
    /// </remarks>
    protected override async Task PublishCore(
        IEnumerable<Func<INotification, CancellationToken, Task>> allHandlers,
        INotification notification,
        CancellationToken cancellationToken)
    {
        var notificationName = notification.GetType().Name;

        foreach (var handler in allHandlers)
        {
            try
            {
                await handler(notification, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogVerbose($"[Bus] A handler for {notificationName} was cancelled.");
            }
            catch (Exception ex)
            {
                // The handler delegate is a closure over the resolved handler instance, so its own
                // type name isn't reachable here -- the exception's stack trace is what identifies
                // the failing handler, hence logging the full exception rather than just Message
                // (the convention elsewhere in this codebase). Without it this line would say only
                // that "something" subscribed to this notification failed.
                _logger.LogError(
                    $"[Bus] A handler for {notificationName} threw; continuing with the remaining " +
                    $"handlers. {ex}");
            }
        }
    }
}
