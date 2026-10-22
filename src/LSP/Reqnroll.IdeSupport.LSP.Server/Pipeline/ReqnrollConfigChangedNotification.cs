using MediatR;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published internally when a <c>reqnroll.json</c> file is created, changed, or deleted
/// in a workspace root. Consumers should re-parse all feature files in the affected workspace.
/// </summary>
/// <remarks>
/// <see cref="Project"/> is set when the changed file belongs to a known project (a <c>reqnroll.json</c>
/// edit). Such a change can alter how every feature file of that project parses (for example
/// <c>language.feature</c>), so the handler reconciles the project's open and closed files. It is left
/// <see langword="null"/> for changes that only affect the open buffers under the root.
/// <b>Consumers must be idempotent:</b> handling the same notification twice must
/// leave the same observable end state as handling it once — <see cref="ReqnrollConfigChangedHandler"/>
/// re-reads the current set of open buffers and reschedules a reparse for each, so a duplicate
/// publish (e.g. a debounced file watcher firing twice for one save) reruns the same work rather
/// than compounding it.
/// </remarks>
public record ReqnrollConfigChangedNotification(string WorkspaceRootPath, LspReqnrollProject? Project = null)
    : INotification;
