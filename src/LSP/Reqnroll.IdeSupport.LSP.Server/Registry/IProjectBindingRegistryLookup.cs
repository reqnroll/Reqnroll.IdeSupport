using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Server.Registry;

/// <summary>
/// Resolves the <see cref="ProjectBindingRegistry"/> for a specific document URI within
/// the LSP server, routing the lookup to the <see cref="ConnectorBindingRegistryProvider"/>
/// that owns the project the document belongs to.
/// </summary>
/// <remarks>
/// This is the correct server-side abstraction for step-definition lookup.
/// Unlike <see cref="IBindingRegistryProvider"/> (which is a per-project interface),
/// this interface is URI-aware so consumers do not need to know which project a
/// document belongs to — that routing is done here via
/// <see cref="Workspace.ILspWorkspaceScopeManager.GetProjectForUri"/>.
/// <para>
/// Registry-change notifications flow through MediatR as
/// <see cref="Pipeline.BindingRegistryReplacedNotification"/> /
/// <see cref="Pipeline.BindingRegistryPatchedNotification"/> (issue #577) rather than through a
/// C# event on this interface, so that all cross-cutting LSP concerns follow the same
/// established notification pattern.
/// </para>
/// </remarks>
public interface IProjectBindingRegistryLookup
{
    /// <summary>
    /// Returns the binding registry for the project that owns <paramref name="uri"/>,
    /// or <see cref="ProjectBindingRegistry.Invalid"/> when the document has no
    /// associated project or the project has not yet completed a discovery run.
    /// </summary>
    ProjectBindingRegistry GetRegistryForUri(DocumentUri uri);

    /// <summary>
    /// Returns <see langword="true"/> when any registry owned by the projects that contain
    /// <paramref name="csUri"/> has a step-definition binding whose source span covers
    /// <paramref name="query"/>. Used by <see cref="Handlers.ProtocolHandlers.ReferencesHandler"/>
    /// to distinguish "no binding at this location" (return <see langword="null"/>) from
    /// "binding with zero matching steps" (return empty).
    /// </summary>
    bool HasBindingAtLocation(DocumentUri csUri, SourceLocation query);

    /// <summary>
    /// Returns a snapshot of all currently-known (project name, owner, registry) triplets,
    /// one per discovered project. Used by Find Unused Step Definitions to enumerate all step
    /// definitions workspace-wide.
    /// </summary>
    IReadOnlyList<(string ProjectName, ProjectOwner Owner, ProjectBindingRegistry Registry)> GetAllRegistries();

    /// <summary>
    /// Resolves the full set of projects whose feature files are legitimate usage sites for the
    /// step-definition bindings declared in <paramref name="csUri"/> — the project filter to pass
    /// to <see cref="Matching.IBindingMatchService.FindUsages(Bindings.BindingId, IReadOnlyCollection{ProjectOwner}?)"/>
    /// or its <c>SourceLocation</c> overload for a "how many/where is this binding used" query
    /// (step-usage CodeLens, Find Step Usages navigation).
    /// </summary>
    /// <remarks>
    /// Starts from the project(s) that directly own <paramref name="csUri"/> (folder-prefix
    /// ownership, resolved internally) and widens that set to include any other project whose own
    /// registry independently reports one of this file's bindings (issue #548): a project that
    /// references another Reqnroll-bearing project (a class library, say) discovers that library's
    /// bindings too via its own connector run, and its feature files are legitimate usage sites for
    /// them even though it doesn't "own" the .cs file that declares them. Direct ownership alone
    /// would undercount to zero for a step used only from the referencing project — the exact
    /// symptom that motivated this method: the step-usage CodeLens (which already applied this
    /// widening internally) reported "1 step usage" while the click-to-navigate command (which
    /// used direct ownership alone) reported zero for the same binding.
    /// <para>
    /// The widening matches on <see cref="Bindings.BindingId"/> <em>and</em> requires the reporting
    /// registry's entry to resolve to the exact same physical <c>SourceLocation.SourceFile</c> —
    /// content-only matching would also widen to a project with its own unrelated but
    /// identical-looking binding (e.g. a parallel multi-targeted sibling project with its own copy
    /// of the same source), which doubled usage counts before this constraint (issue #552).
    /// </para>
    /// Returns <see langword="null"/> (unrestricted search) when <paramref name="csUri"/> has no
    /// direct owner at all, preserving prior behaviour for an unowned file.
    /// </remarks>
    IReadOnlyCollection<ProjectOwner>? ResolveUsageSearchScope(DocumentUri csUri);
}
