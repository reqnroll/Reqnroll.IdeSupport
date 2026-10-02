using System;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Thrown by <see cref="VsUtils.GetInstalledNuGetPackages"/> when NuGet's brokered
/// <c>INuGetProjectService</c> reports <c>InstalledPackageResultStatus.ProjectNotReady</c> for the
/// project -- it hasn't been nominated/restored yet. Routine during solution load (issue #690), not
/// a real failure: callers should treat it as "package references aren't known yet, try again once
/// restore finishes" rather than logging it at Warning alongside genuinely unexpected failures.
/// </summary>
public sealed class NuGetProjectNotReadyException : Exception
{
    /// <summary>Creates the exception with a fixed, descriptive message.</summary>
    public NuGetProjectNotReadyException()
        : base("NuGet reports the project is not ready yet (not nominated/restored).")
    {
    }
}
