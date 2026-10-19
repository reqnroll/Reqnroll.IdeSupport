using MediatR;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <summary>
/// Published when a project's <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry"/>
/// is replaced wholesale — a full connector discovery run (startup or post-build), a deferred
/// full re-scan firing once a project's membership baseline and its <c>reqnroll/projectLoaded</c>
/// registration have both arrived, or the membership index's own baseline arriving.
/// </summary>
/// <remarks>
/// <para>
/// This is one of three registry-change events, each named for its producer's intent instead of
/// being a flag value a reader has to trace back to its source to interpret. See
/// <see cref="BindingRegistryPatchedNotification"/> for the incremental counterpart and
/// <see cref="ProjectBindingFilesRemovedNotification"/> for the binding-file-removal command.
/// </para>
/// <para>
/// Consumers should re-parse <em>all</em> workspace feature files that belong to
/// <see cref="Project"/> — not only the currently open ones — so that the binding match cache
/// covers the complete workspace for features such as Find Step Definition Usages / Find All
/// References.
/// </para>
/// <para>
/// <b>Consumers must be idempotent:</b> handling the same notification twice for the
/// same <see cref="Project"/> must leave the same observable end state as handling it once.
/// </para>
/// </remarks>
public sealed record BindingRegistryReplacedNotification(LspReqnrollProject Project) : INotification;
