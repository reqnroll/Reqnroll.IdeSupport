using MediatR;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published when a project's <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry"/>
/// is replaced after a successful connector discovery run (e.g. triggered by a build or a
/// <c>reqnroll.json</c> change).
/// </summary>
/// <remarks>
/// When <see cref="IsFullReplacement"/> is <see langword="true"/> (e.g. startup or a post-build
/// reflection discovery run), consumers should re-parse <em>all</em> workspace feature files that
/// belong to <see cref="Project"/> — not only the currently open ones — so that the binding match
/// cache covers the complete workspace for features such as Find Step Definition Usages / Find
/// All References.
/// When <see cref="IsFullReplacement"/> is <see langword="false"/> (incremental Roslyn re-discovery
/// on a <c>.cs</c> edit), re-parsing only the open feature files is sufficient immediately, and
/// closed feature files are additionally rescanned after a debounce so their cached usage counts
/// stay correct without waiting for a rebuild. This notification is only published for an
/// incremental patch when it actually changed a binding's matched expression -- a patch that
/// didn't (e.g. a method-body edit) never reaches here, since there is nothing to re-parse.
/// <para>
/// <b>Consumers must be idempotent (issue #578):</b> handling the same notification twice —
/// same <see cref="Project"/>, same <see cref="IsFullReplacement"/>, same
/// <see cref="RemovedBindingFilePaths"/> — must leave the same observable end state as handling
/// it once. Every current handler re-derives its result from current state (a full rescan, or an
/// unconditional re-push of every open file's diagnostics) rather than applying an incremental
/// delta, so a duplicate publish is naturally a no-op beyond redoing the same work.
/// </para>
/// </remarks>
public record BindingRegistryChangedNotification(
    LspReqnrollProject Project,
    bool IsFullReplacement = false,
    IReadOnlyCollection<string>? RemovedBindingFilePaths = null) : INotification;
