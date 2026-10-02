using MediatR;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published when binding-role <c>.cs</c> files leave a project's membership index — e.g. the
/// user deletes a step-definition file in the IDE (issue #94) — instructing consumers to purge
/// the stale entries those files contributed to <see cref="Project"/>'s
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// One of three events that previously shared a single <c>BindingRegistryChangedNotification</c>
/// type, where <see cref="Paths"/> rode along as an optional field on a notification named as a
/// fact but carrying an instruction (issue #577) — see
/// <see cref="BindingRegistryReplacedNotification"/>'s remarks for the full rationale. Unlike its
/// two siblings this one is a genuine command, not a fact: it does not merely report that
/// something changed, it tells the consumer what to remove.
/// </para>
/// <para>
/// Visual Studio never sends <c>workspace/didChangeWatchedFiles</c> for a binding-file deletion,
/// so <see cref="WatchedFilesHandler"/>'s deletion path never fires for it; a
/// <c>reqnroll/projectFiles</c> delta reporting the removal is what actually reaches VS, and is
/// the sole producer of this notification.
/// </para>
/// <para>
/// <b>Consumers must be idempotent (issue #578):</b> handling the same notification twice with
/// the same <see cref="Project"/>/<see cref="Paths"/> must leave the same observable end state as
/// handling it once — removing an already-removed file's (now empty) entries is a no-op.
/// </para>
/// </remarks>
public sealed record ProjectBindingFilesRemovedNotification(
    LspReqnrollProject Project,
    IReadOnlyCollection<string> Paths) : INotification;
