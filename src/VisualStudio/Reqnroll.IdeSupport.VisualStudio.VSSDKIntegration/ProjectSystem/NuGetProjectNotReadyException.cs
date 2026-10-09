using System;

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

/// <summary>
/// Thrown by <see cref="VsUtils.GetInstalledNuGetPackages"/> when NuGet's brokered
/// <c>INuGetProjectService</c> reports <c>InstalledPackageResultStatus.ProjectNotReady</c> for the
/// project -- it hasn't been nominated/restored yet. Routine during solution load (issue #690), not
/// a real failure: callers should treat it as "package references aren't known yet, try again once
/// restore finishes" rather than logging it at Warning alongside genuinely unexpected failures.
/// </summary>
/// <remarks>
/// Also thrown, with <see cref="TimedOut"/> set, when the service does not answer within
/// <see cref="VsUtils.NuGetInstalledPackagesTimeout"/> (issue #1031): the package list is just as
/// unknown, so callers retry it the same way. Unlike a plain ProjectNotReady answer, a timeout is
/// unexpected and worth a Warning.
/// </remarks>
public sealed class NuGetProjectNotReadyException : Exception
{
    /// <summary>Creates the exception with a fixed, descriptive message.</summary>
    public NuGetProjectNotReadyException()
        : base("NuGet reports the project is not ready yet (not nominated/restored).")
    {
    }

    /// <summary>Creates the exception for a NuGet service that did not answer within <paramref name="timeout"/>.</summary>
    public NuGetProjectNotReadyException(TimeSpan timeout)
        : base($"NuGet did not report the project's installed packages within {timeout.TotalSeconds:0.#}s.")
    {
        TimedOut = true;
    }

    /// <summary>
    /// <see langword="true"/> when NuGet did not answer in time, rather than answering
    /// <c>ProjectNotReady</c>.
    /// </summary>
    public bool TimedOut { get; }
}
