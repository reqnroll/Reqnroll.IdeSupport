using MediatR;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Re-checks a project for Reqnroll whenever its binding registry is replaced, which follows every
/// build. See <see cref="ReqnrollProjectPresenceNotifier"/> for why the check on first report is
/// not enough.
/// </summary>
public sealed class ReqnrollProjectPresenceHandler : INotificationHandler<BindingRegistryReplacedNotification>
{
    private readonly ReqnrollProjectPresenceNotifier _notifier;

    /// <summary>Initializes a new instance of the <see cref="ReqnrollProjectPresenceHandler"/> class.</summary>
    public ReqnrollProjectPresenceHandler(ReqnrollProjectPresenceNotifier notifier)
    {
        _notifier = notifier;
    }

    /// <inheritdoc/>
    public Task Handle(BindingRegistryReplacedNotification notification, CancellationToken cancellationToken)
    {
        _notifier.Evaluate(notification.Project);
        return Task.CompletedTask;
    }
}
