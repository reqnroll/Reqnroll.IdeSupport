using MediatR;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published when a project's <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry"/>
/// receives an incremental, in-place patch rather than a wholesale replacement — an immediate
/// Roslyn source-level update from a live <c>.cs</c> edit, or a membership-index delta that may
/// have re-attributed an already-open buffer's ownership.
/// </summary>
/// <remarks>
/// <para>
/// One of three events that previously shared a single <c>BindingRegistryChangedNotification</c>
/// type (issue #577) — see <see cref="BindingRegistryReplacedNotification"/>'s remarks for why
/// they were split.
/// </para>
/// <para>
/// Re-parsing only the currently open feature files owned by <see cref="Project"/> is sufficient
/// immediately; closed feature files are additionally rescanned after a debounce so their cached
/// usage counts stay correct without waiting for a rebuild. This notification is only published
/// for a Roslyn patch when it actually changed a binding's matched expression — a patch that
/// didn't (e.g. a method-body edit) never reaches here, since there is nothing to re-parse.
/// </para>
/// <para>
/// <b>Consumers must be idempotent (issue #578):</b> handling the same notification twice for the
/// same <see cref="Project"/> must leave the same observable end state as handling it once.
/// </para>
/// </remarks>
public sealed record BindingRegistryPatchedNotification(LspReqnrollProject Project) : INotification;
