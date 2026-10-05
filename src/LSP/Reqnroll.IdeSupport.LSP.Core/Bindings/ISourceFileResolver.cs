#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Maps a source-file path recorded by binding discovery onto a path that exists on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The reflection connector reports whatever the PDB says, which is the absolute path from the
/// machine that compiled the assembly. That is routinely not a path this machine has (issue #540):
/// a devcontainer build records <c>/workspaces/host-solution/…</c>; a deterministic CI build
/// (<c>ContinuousIntegrationBuild=true</c>) records <c>/_/…</c>; an external binding assembly from
/// NuGet was built on someone else's machine by definition; and any solution built on one box and
/// opened on another has the same shape.
/// </para>
/// <para>
/// Implementations answer with a local path or <see langword="null"/>. <see langword="null"/> is a
/// real answer, not a failure — see <see cref="Documents.SourceLocation.IsResolved"/> for what
/// callers must then do with it.
/// </para>
/// </remarks>
public interface ISourceFileResolver
{
    /// <summary>
    /// Returns a path on this machine for <paramref name="recordedPath"/>, or <see langword="null"/>
    /// when no local file can be identified for it.
    /// </summary>
    string? Resolve(string? recordedPath);
}
