using System.IO;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// Shared helper: show a picker when a navigation query returns multiple results, then open
/// the chosen file in the editor. Works for Go to Hooks and future ambiguous-step-
/// definition navigation (Go to Step Definition — multiple matching bindings).
/// </summary>
/// <remarks>
/// 0 targets → no-op (caller logs this case).
/// 1 target  → navigates directly without a picker.
/// N targets → shows a <see cref="NavigationPickerDialog"/>; navigates to the selection.
/// </remarks>
internal static class NavigationPickerHelper
{
    /// <summary>
    /// Navigates directly for a single target; shows a picker dialog and navigates to the
    /// selection for multiple targets; no-ops for zero targets.
    /// </summary>
    /// <param name="originatingFilePath">
    /// The file the navigation started from (the <c>.feature</c> file). Required to open a target by
    /// its <see cref="NavigationTarget.Identity"/>, which is resolved in the project that owns this file.
    /// </param>
    public static async Task PickAndNavigateAsync(
        IReadOnlyList<NavigationTarget> targets,
        IIdeSupportLogger                 logger,
        string                          promptTitle,
        CancellationToken               cancellationToken,
        string?                         originatingFilePath = null)
    {
        if (targets.Count == 0)
            return;

        NavigationTarget chosen;

        if (targets.Count == 1)
        {
            chosen = targets[0];
        }
        else
        {
            // Dialog must be created and shown on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dialog = new NavigationPickerDialog(promptTitle, targets);
            if (dialog.ShowModal() != true || dialog.SelectedIndex < 0)
            {
                logger.LogVerbose("NavigationPickerHelper: picker dismissed, no navigation.");
                return;
            }

            chosen = targets[dialog.SelectedIndex];
        }

        if (chosen.FilePath is not { Length: > 0 } && chosen.Identity is not null && originatingFilePath is not null)
        {
            if (!await ExternalBindingNavigator
                    .NavigateAsync(chosen.Identity, originatingFilePath, logger, cancellationToken)
                    .ConfigureAwait(false))
                await Reqnroll.IdeSupport.VisualStudio.Utilities.VsUtils.ShowStatusBarMessageAsync(
                    $"Reqnroll: could not open {chosen.DisplayText} - its source is not on this machine and Visual Studio " +
                    "could not navigate to it (rebuild the project, or enable navigation to decompiled sources).",
                    cancellationToken);
            return;
        }

        await NavigateToAsync(chosen, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NavigateToAsync(
        NavigationTarget  target,
        IIdeSupportLogger   logger,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.FilePath))
        {
            logger.LogWarning($"NavigationPickerHelper: file not found: '{target.FilePath}'");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as DTE2;
        if (dte is null)
        {
            logger.LogWarning("NavigationPickerHelper: DTE service not available.");
            return;
        }

        var window = dte.ItemOperations.OpenFile(target.FilePath, EnvDTE.Constants.vsViewKindTextView);
        window.Activate();

        if (window.Document?.Selection is EnvDTE.TextSelection selection)
        {
            // NavigationTarget uses 0-based coordinates; DTE TextSelection is 1-based.
            selection.MoveToLineAndOffset(target.StartLine + 1, target.StartChar + 1);
        }

        logger.LogInfo(
            $"NavigationPickerHelper: navigated to '{target.DisplayText}' at {target.FilePath}:{target.StartLine + 1}");
    }
}
