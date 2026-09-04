using MediatR;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published internally when a <c>reqnroll.json</c> file is created, changed, or deleted
/// in a workspace root. Consumers should re-parse all feature files in the affected workspace.
/// </summary>
/// <remarks>
/// <b>Consumers must be idempotent (issue #578):</b> handling the same notification twice must
/// leave the same observable end state as handling it once — <see cref="ReqnrollConfigChangedHandler"/>
/// re-reads the current set of open buffers and reschedules a reparse for each, so a duplicate
/// publish (e.g. a debounced file watcher firing twice for one save) reruns the same work rather
/// than compounding it.
/// </remarks>
public record ReqnrollConfigChangedNotification(string WorkspaceRootPath) : INotification;
