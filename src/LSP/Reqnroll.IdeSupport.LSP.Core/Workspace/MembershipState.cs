namespace Reqnroll.IdeSupport.LSP.Core.Workspace;

/// <summary>
/// Describes how a URI relates to the authoritative project-membership index populated by
/// <c>reqnroll/projectFiles</c> notifications.
/// </summary>
public enum MembershipState
{
    /// <summary>
    /// At least one project has claimed the file in a received baseline payload.
    /// Binding-dependent features (step matching, diagnostics, Go-to-Definition) are available.
    /// </summary>
    Owned,

    /// <summary>
    /// No project has claimed the file yet, but at least one project that could cover this
    /// path has not sent a baseline.  The server treats the file as best-effort: folder-prefix
    /// fallback is used so startup does not regress until baselines arrive.
    /// </summary>
    Pending,

    /// <summary>
    /// No project claims the file and every covering project has already sent its baseline.
    /// The file is intentionally excluded from all projects.  Binding-dependent features are
    /// suppressed to avoid phantom diagnostics (invariant I2).
    /// </summary>
    Unowned,
}
