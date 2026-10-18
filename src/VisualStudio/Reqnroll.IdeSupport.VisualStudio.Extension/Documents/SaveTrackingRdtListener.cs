#nullable enable

using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Documents;

/// <summary>
/// Feeds <see cref="RecentSaveTracker.Instance"/> from <c>IVsRunningDocTableEvents3.OnBeforeSave</c>,
/// so the Format Document command filter can tell a format fired from the save sequence (a
/// format-on-save extension, Code Cleanup on save) from an explicit one and never show a dialog for the
/// former (issue #1132).
/// </summary>
/// <remarks>
/// The moniker is read through <see cref="IVsRunningDocumentTable4.GetDocumentMoniker"/>, which — unlike
/// <c>IVsRunningDocumentTable.GetDocumentInfo</c> — does not materialise a delayed-load document stub
/// (see <see cref="DocumentInitializationMonitor"/>). All members must be called on the UI thread, which
/// is where the RDT raises its events.
/// </remarks>
internal sealed class SaveTrackingRdtListener : IVsRunningDocTableEvents3, IDisposable
{
    private readonly IVsRunningDocumentTable _rdt;
    private readonly IIdeSupportLogger _logger;
    private uint _cookie;
    private bool _disposed;

    private SaveTrackingRdtListener(IVsRunningDocumentTable rdt, IIdeSupportLogger logger)
    {
        _rdt = rdt;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to RDT events. Returns <see langword="null"/> if the RDT is unavailable or the
    /// subscription fails — best-effort, and must never break package initialization.
    /// </summary>
    public static SaveTrackingRdtListener? TryAdvise(IVsRunningDocumentTable? rdt, IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (rdt is null)
            return null;

        var listener = new SaveTrackingRdtListener(rdt, logger);
        try
        {
            rdt.AdviseRunningDocTableEvents(listener, out listener._cookie);
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "SaveTrackingRdtListener: AdviseRunningDocTableEvents failed.");
            return null;
        }

        return listener;
    }

    /// <inheritdoc />
    public int OnBeforeSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (_rdt is IVsRunningDocumentTable4 rdt4)
                RecentSaveTracker.Instance.SaveBegan(rdt4.GetDocumentMoniker(docCookie));
        }
        catch (Exception ex)
        {
            // Never let a diagnostic helper break a save.
            _logger.LogException(ex, "SaveTrackingRdtListener: OnBeforeSave failed.");
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc />
    public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld,
        string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining,
        uint dwEditLocksRemaining) => VSConstants.S_OK;

    /// <inheritdoc />
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining,
        uint dwEditLocksRemaining) => VSConstants.S_OK;

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
            _logger.LogException(ex, "SaveTrackingRdtListener: UnadviseRunningDocTableEvents failed.");
        }
        finally
        {
            _cookie = 0;
        }
    }
}
