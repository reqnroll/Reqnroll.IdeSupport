#nullable enable

using System;
using System.Diagnostics;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>
/// Decides, from a Running Document Table attribute-change notification, whether a
/// <c>.feature</c> document just finished initializing.
/// </summary>
/// <remarks>
/// Split out from <see cref="FeatureDocumentInitializationMonitor"/> so the rule is testable
/// without a live RDT.
/// </remarks>
internal static class RdtDocumentInitialization
{
    /// <summary>
    /// The RDT attribute VS raises when a document that had <c>RDT_PendingInitialization</c>
    /// (a restored "stub" document) has completed its initialization, or when a document is
    /// added to the RDT already fully initialized.
    /// </summary>
    public const uint DocumentInitializedAttribute = (uint)__VSRDTATTRIB3.RDTA_DocumentInitialized;

    /// <summary>
    /// True when <paramref name="attributes"/> reports a completed initialization and
    /// <paramref name="moniker"/> is a <c>.feature</c> file.
    /// </summary>
    public static bool IsFeatureDocumentInitialization(string? moniker, uint attributes) =>
        (attributes & DocumentInitializedAttribute) != 0 && IsFeatureFile(moniker);

    /// <summary>True when the document moniker names a <c>.feature</c> file.</summary>
    public static bool IsFeatureFile(string? moniker) =>
        !string.IsNullOrEmpty(moniker)
        && moniker!.EndsWith(".feature", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Logs when restored <c>.feature</c> "stub" documents are realized by VS, and what the stub
/// inventory looks like at package load (issue #533, phase 2).
/// </summary>
/// <remarks>
/// <para>
/// Instrumentation, not behaviour: this class never forces a document to initialize and never
/// touches the <c>LanguageServerProvider</c>. It exists to make the gap the issue is about
/// measurable — the wall-clock distance between "package loaded" and "the restored feature tab
/// became a real document" — and to give phase 3 a precise hook to attach to once the forcing
/// logic moves here from <see cref="ReqnrollLanguageClient.OnServerInitializationResultAsync"/>.
/// </para>
/// <para>
/// The inventory scan deliberately goes through <see cref="IVsRunningDocumentTable4"/>
/// (<c>GetDocumentMoniker</c> + <c>GetDocumentFlags</c>) rather than
/// <see cref="IVsRunningDocumentTable.GetDocumentInfo"/>: the latter always materialises the doc
/// data, creating it if necessary, which would initialize the very stubs we are trying to observe.
/// See <see href="https://learn.microsoft.com/visualstudio/extensibility/internals/delayed-document-loading">Delayed document loading</see>.
/// </para>
/// <para>All members must be called on the UI thread.</para>
/// </remarks>
internal sealed class FeatureDocumentInitializationMonitor : IVsRunningDocTableEvents2, IDisposable
{
    private readonly IVsRunningDocumentTable _rdt;
    private readonly IIdeSupportLogger _logger;
    private readonly Stopwatch _sinceAdvise;
    private uint _cookie;
    private bool _disposed;

    private FeatureDocumentInitializationMonitor(IVsRunningDocumentTable rdt, IIdeSupportLogger logger)
    {
        _rdt = rdt;
        _logger = logger;
        _sinceAdvise = Stopwatch.StartNew();
    }

    /// <summary>
    /// Subscribes to RDT events and logs the current <c>.feature</c> stub inventory. Returns
    /// <see langword="null"/> if the RDT is unavailable or the subscription fails — this is a
    /// diagnostic aid and must never break package initialization.
    /// </summary>
    public static FeatureDocumentInitializationMonitor? TryAdvise(
        IVsRunningDocumentTable? rdt,
        IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (rdt is null)
        {
            logger.LogInfo("FeatureDocumentInitializationMonitor: RDT unavailable; not advising.");
            return null;
        }

        var monitor = new FeatureDocumentInitializationMonitor(rdt, logger);
        try
        {
            rdt.AdviseRunningDocTableEvents(monitor, out monitor._cookie);
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "FeatureDocumentInitializationMonitor: AdviseRunningDocTableEvents failed.");
            return null;
        }

        monitor.LogPendingFeatureDocuments();
        return monitor;
    }

    /// <summary>
    /// Logs every <c>.feature</c> document currently in the RDT and whether it is still a stub.
    /// This is the phase 0 evidence: if a restored feature tab shows as pending here while the
    /// user sees no LSP features, the stub-frame diagnosis holds.
    /// </summary>
    private void LogPendingFeatureDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_rdt is not IVsRunningDocumentTable4 rdt4)
        {
            _logger.LogInfo("FeatureDocumentInitializationMonitor: RDT does not implement IVsRunningDocumentTable4; skipping stub inventory.");
            return;
        }

        try
        {
            _rdt.GetRunningDocumentsEnum(out var docs);
            if (docs is null)
                return;

            var cookies = new uint[1];
            var featureCount = 0;
            var stubCount = 0;

            while (docs.Next(1, cookies, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                var cookie = cookies[0];

                // Both calls are stub-safe: neither creates the doc data.
                var moniker = rdt4.GetDocumentMoniker(cookie);
                if (!RdtDocumentInitialization.IsFeatureFile(moniker))
                    continue;

                featureCount++;
                var flags = (_VSRDTFLAGS4)rdt4.GetDocumentFlags(cookie);
                var pending = (flags & _VSRDTFLAGS4.RDT_PendingInitialization) != 0;
                if (pending)
                    stubCount++;

                _logger.LogInfo(
                    $"FeatureDocumentInitializationMonitor: {moniker} — {(pending ? "STUB (pending initialization)" : "initialized")}.");
            }

            _logger.LogInfo(
                $"FeatureDocumentInitializationMonitor: {featureCount} .feature document(s) in the RDT at advise time, {stubCount} still stubs.");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "FeatureDocumentInitializationMonitor: stub inventory scan failed.");
        }
    }

    /// <inheritdoc />
    public int OnAfterAttributeChangeEx(
        uint docCookie,
        uint grfAttribs,
        IVsHierarchy pHierOld,
        uint itemidOld,
        string pszMkDocumentOld,
        IVsHierarchy pHierNew,
        uint itemidNew,
        string pszMkDocumentNew)
    {
        // pszMkDocumentNew is only populated for renames; fall back to the old moniker, which is
        // the one carried on a plain initialization notification.
        var moniker = string.IsNullOrEmpty(pszMkDocumentNew) ? pszMkDocumentOld : pszMkDocumentNew;

        if (RdtDocumentInitialization.IsFeatureDocumentInitialization(moniker, grfAttribs))
        {
            _logger.LogInfo(
                $"FeatureDocumentInitializationMonitor: {moniker} initialized {_sinceAdvise.ElapsedMilliseconds}ms after package load.");
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc />
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnAfterSave(uint docCookie) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_cookie == 0)
            return;

        try
        {
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _rdt.UnadviseRunningDocTableEvents(_cookie);
            });
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "FeatureDocumentInitializationMonitor: UnadviseRunningDocTableEvents failed.");
        }
        finally
        {
            _cookie = 0;
        }
    }
}
