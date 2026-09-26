#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Recovers a missed language server activation (issue #533) by opening and closing a throwaway
/// <c>.feature</c> file.
/// </summary>
/// <remarks>
/// <para>
/// VS activates a <c>LanguageServerProvider</c> when a matching document is <em>opened</em>, and
/// does not re-check documents that are already open. On the first launch after the extension is
/// installed or updated, VS can restore a <c>.feature</c> tab before it knows about the provider.
/// That tab then gets no language server features for the whole session. Clicking or editing it
/// does not help. Only a new document open does.
/// </para>
/// <para>
/// VisualStudio.Extensibility has no "activate now" API, so this supplies a document open instead.
/// Activation applies to the whole provider, not one document: once activated, VS sends
/// <c>textDocument/didOpen</c> for every matching document already open, including the restored
/// tab. This was confirmed by hand on 2026-08-31 by opening an unrelated <c>.feature</c> file. So
/// the user's own document is never closed or reopened, and its undo history and unsaved edits
/// are not touched.
/// </para>
/// <para>
/// Runs at most once per session, and only when all of these hold after a grace period:
/// the trigger is not turned off with <see cref="ActivationTriggerRules.DisableEnvironmentVariable"/>,
/// VS has not activated the provider (<see cref="LanguageServerActivationSignal"/>), and at least one
/// <c>.feature</c> document is open. On a normal start VS activates the provider within ~1.5 s, so
/// the trigger does nothing.
/// </para>
/// <para>
/// The trigger checks only for <c>.feature</c> documents, although <c>.cs</c> is also in the
/// provider's <c>AppliesTo</c>. The failure was only ever measured for <c>.feature</c> tabs, and
/// checking <c>.cs</c> too would open a Reqnroll file in solutions that do not use Reqnroll.
/// </para>
/// <para>
/// The scratch tab is shown and then closed, so it can flicker briefly. Opening it without a
/// window (for example with <c>IVsInvisibleEditorManager</c>) might not produce the open that VS
/// reacts to, and has not been tested.
/// </para>
/// </remarks>
internal static class ScratchFileActivationTrigger
{
    /// <summary>How long VS gets to activate the provider on its own, measured from solution load.</summary>
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>How long the scratch file stays open while VS activates the provider.</summary>
    internal static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(10);

    internal const string ScratchFileName = "ReqnrollActivation.feature";

    internal const string ScratchFileContent =
        "# Opened and closed automatically by the Reqnroll extension to start its language server.\r\n" +
        "# Safe to delete; it is re-created when needed.\r\n" +
        "Feature: Reqnroll language server activation\r\n";

    /// <summary>
    /// Waits out the grace period, then opens and closes the scratch file if activation was missed.
    /// Call after the solution has loaded. Failures are logged, not thrown, except cancellation.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider serviceProvider,
        LanguageServerActivationSignal signal,
        IIdeSupportLogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ActivationTriggerRules.IsDisabled())
            {
                logger.LogInfo(
                    $"ScratchFileActivationTrigger: disabled by {ActivationTriggerRules.DisableEnvironmentVariable}.");
                return;
            }

            if (await signal.WaitAsync(GracePeriod, cancellationToken).ConfigureAwait(false))
            {
                logger.LogVerbose("ScratchFileActivationTrigger: provider already activated; nothing to do.");
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var openFeatureDocuments = CountOpenFeatureDocuments(serviceProvider);
            var decision = ActivationTriggerRules.Decide(
                disabled: false, signal.IsActivated, openFeatureDocuments);

            if (decision != ActivationTriggerDecision.Trigger)
            {
                logger.LogVerbose($"ScratchFileActivationTrigger: {decision}; nothing to do.");
                return;
            }

            logger.LogInfo(
                $"ScratchFileActivationTrigger: provider not activated {DurationFormatter.FormatMilliseconds(GracePeriod)} " +
                $"after solution load with {openFeatureDocuments} .feature document(s) open; opening a scratch .feature file.");

            await OpenAndCloseScratchFileAsync(serviceProvider, signal, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "ScratchFileActivationTrigger: failed.");
        }
    }

    private static async Task OpenAndCloseScratchFileAsync(
        IServiceProvider serviceProvider,
        LanguageServerActivationSignal signal,
        IIdeSupportLogger logger,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var path = EnsureScratchFile();

        // A scratch tab left over from a crashed session is already open, so opening it again
        // would not be a new open. Close it first; the file is ours and never has user edits.
        if (VsShellUtilities.IsDocumentOpen(serviceProvider, path, Guid.Empty, out _, out _, out var staleFrame))
        {
            logger.LogInfo("ScratchFileActivationTrigger: scratch file was already open; closing it before reopening.");
            staleFrame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        var stopwatch = Stopwatch.StartNew();
        VsShellUtilities.OpenDocument(serviceProvider, path, VSConstants.LOGVIEWID_Primary, out _, out _, out var frame);

        try
        {
            // Show it like a normal open. The fix was verified with a shown tab, and a frame that
            // is never shown may not produce the open VS reacts to.
            frame?.Show();

            var activated = await signal.WaitAsync(ActivationTimeout, cancellationToken);
            if (activated)
            {
                logger.LogInfo(
                    $"ScratchFileActivationTrigger: provider activated {DurationFormatter.FormatMilliseconds(stopwatch.Elapsed)} after opening the scratch file.");
            }
            else
            {
                logger.LogWarning(
                    $"ScratchFileActivationTrigger: provider still not activated {DurationFormatter.FormatMilliseconds(ActivationTimeout)} " +
                    "after opening the scratch file. Close and reopen the .feature file to get language features.");
            }
        }
        finally
        {
            // Always close the scratch tab, even on cancellation.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }
    }

    /// <summary>Writes the scratch file under the temp folder unless it already has the expected content.</summary>
    internal static string EnsureScratchFile(string? tempRoot = null)
    {
        var directory = Path.Combine(tempRoot ?? Path.GetTempPath(), "Reqnroll");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, ScratchFileName);
        if (!File.Exists(path) || File.ReadAllText(path) != ScratchFileContent)
            File.WriteAllText(path, ScratchFileContent);

        return path;
    }

    /// <summary>Counts the <c>.feature</c> documents in the Running Document Table without loading any of them.</summary>
    private static int CountOpenFeatureDocuments(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // IVsRunningDocumentTable4.GetDocumentMoniker does not create doc data, unlike
        // IVsRunningDocumentTable.GetDocumentInfo; see DocumentInitializationMonitor.
        if (serviceProvider.GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable rdt
            || rdt is not IVsRunningDocumentTable4 rdt4)
            return 0;

        rdt.GetRunningDocumentsEnum(out var docs);
        if (docs is null)
            return 0;

        var cookies = new uint[1];
        var count = 0;
        while (docs.Next(1, cookies, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            if (RdtDocumentInitialization.Classify(rdt4.GetDocumentMoniker(cookies[0])) == RdtDocumentKind.Feature)
                count++;
        }

        return count;
    }
}
