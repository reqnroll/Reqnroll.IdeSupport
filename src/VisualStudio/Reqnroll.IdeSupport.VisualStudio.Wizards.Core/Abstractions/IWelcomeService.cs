using Reqnroll.IdeSupport.Common;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Orchestrates the first-install welcome flow and the upgrade changelog flow.
/// Called early during extension startup (from ReqnrollPluginPackage).
/// Implemented in the VsIntegration layer (Reqnroll.IdeSupport.VisualStudio.Wizards).
/// </summary>
public interface IWelcomeService
{
    /// <summary>
    /// Checks the install status from the registry and shows the
    /// Welcome dialog (first install) or Upgrade dialog (version change).
    /// The dialog is shown after a short delay (7 seconds) to allow
    /// VS to finish loading.
    /// </summary>
    void OnIdeScopeActivityStarted(IIdeScope ideScope);

    /// <summary>
    /// Counts today as a day of use, once the language server reports a project that uses
    /// Reqnroll. Once per day, and not on the install day.
    /// </summary>
    void OnReqnrollProjectDetected(IIdeScope ideScope);
}
