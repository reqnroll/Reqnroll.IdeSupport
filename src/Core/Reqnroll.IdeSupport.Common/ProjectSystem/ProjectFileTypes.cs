#nullable disable

using System;
using System.IO;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>Classifies a project file by its extension.</summary>
public static class ProjectFileTypes
{
    // A C# shared project. Its own file declares no output; the .projitems beside it is what
    // referencing projects <Import>, which is how its sources get compiled -- into each
    // referencing project's assembly, never into one of its own.
    private const string SharedProjectExtension = ".shproj";

    // The item file a shared project carries. Never a project in its own right, and named here
    // because MSBuild-evaluating clients can surface it as the "project file" of a shared item
    // group.
    private const string SharedItemsExtension = ".projitems";

    /// <summary>
    /// Returns whether <paramref name="projectFilePath"/> names a shared project rather than a
    /// buildable one.
    /// </summary>
    /// <remarks>
    /// A shared project has no output assembly and no bindings of its own: its sources compile
    /// into every project that imports it. Registering one as a project therefore creates a
    /// project whose binding registry can never be populated, and -- because it is the innermost
    /// folder containing its own files -- one that wins the primary-owner rule for them, so those
    /// files resolve to that empty registry instead of the referencing test project's populated
    /// one (issue #735). Its content still reaches the server, as part of the membership of each
    /// project that imports it.
    /// </remarks>
    public static bool IsSharedProject(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
            return false;

        var extension = Path.GetExtension(projectFilePath);
        return extension.Equals(SharedProjectExtension, StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(SharedItemsExtension, StringComparison.OrdinalIgnoreCase);
    }
}
