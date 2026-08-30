#nullable enable

using System;
using System.Diagnostics;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>The kind of document a Running Document Table moniker names, for logging purposes.</summary>
internal enum RdtDocumentKind
{
    /// <summary>Anything the extension does not act on.</summary>
    Other,

    /// <summary>A Gherkin <c>.feature</c> file.</summary>
    Feature,

    /// <summary>A C# file — a potential binding source, and (since issue #533 phase 1) an activation trigger.</summary>
    CSharp,
}

/// <summary>
/// Reads Running Document Table monikers and attribute-change flags: what kind of document a
/// moniker names, and whether a notification reports a completed initialization.
/// </summary>
/// <remarks>
/// Split out from <see cref="DocumentInitializationMonitor"/> so the rules are testable without
/// a live RDT.
/// </remarks>
internal static class RdtDocumentInitialization
{
    /// <summary>
    /// The RDT attribute VS raises when a document that had <c>RDT_PendingInitialization</c>
    /// (a restored "stub" document) has completed its initialization, or when a document is
    /// added to the RDT already fully initialized.
    /// </summary>
    public const uint DocumentInitializedAttribute = (uint)__VSRDTATTRIB3.RDTA_DocumentInitialized;

    /// <summary>True when <paramref name="attributes"/> reports a completed initialization for a named document.</summary>
    public static bool IsDocumentInitialization(string? moniker, uint attributes) =>
        (attributes & DocumentInitializedAttribute) != 0 && !string.IsNullOrEmpty(moniker);

    /// <summary>Classifies a document moniker by extension.</summary>
    public static RdtDocumentKind Classify(string? moniker)
    {
        if (string.IsNullOrEmpty(moniker))
            return RdtDocumentKind.Other;

        if (moniker!.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            return RdtDocumentKind.Feature;

        if (moniker.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return RdtDocumentKind.CSharp;

        return RdtDocumentKind.Other;
    }

    /// <summary>True for the document kinds that can activate the language server provider.</summary>
    public static bool IsActivationRelevant(string? moniker) =>
        Classify(moniker) != RdtDocumentKind.Other;
}

/// <summary>
/// Logs the Running Document Table's document inventory at package load and every subsequent
/// lifecycle event for the documents that can activate the language server provider — locks,
/// window shows and hides, and initialization — each timestamped relative to package load, so the
/// distance between "package loaded", "the document became real", "the tab was shown" and "VS
/// activated the language server provider" is measurable (issue #533).
/// </summary>
/// <remarks>
/// <para>
/// Instrumentation, not behaviour: this class never forces a document to initialize and never
/// touches the <c>LanguageServerProvider</c>.
/// </para>
/// <para>
/// Originally scoped to <c>.feature</c> files only, and to initialization events only. Widened
/// twice as measurement kept outrunning it (all 2026-08-30):
/// </para>
/// <list type="number">
/// <item>
/// To all activation-relevant documents, after three runs showed the restored <c>.feature</c>
/// document already initialized 76ms after extension load with zero stubs — refuting the
/// stub-frame theory while leaving the co-restored <c>.cs</c> tab's state unmeasured.
/// </item>
/// <item>
/// To every window show/hide and document lock, after a cold run showed the provider activating
/// 244ms after a <em>first</em> show — which turned out to be the user closing and reopening the
/// file, not clicking the restored tab. First-show-only logging could not tell those apart, and
/// the restored tab's own show had happened before this monitor was even advised. Logging every
/// show makes a plain tab click visible, which is what separates "focus re-triggers activation"
/// from "only a fresh document open does".
/// </item>
/// </list>
/// <para>
/// The inventory scan deliberately goes through <see cref="IVsRunningDocumentTable4"/>
/// (<c>GetDocumentMoniker</c> + <c>GetDocumentFlags</c>) rather than
/// <see cref="IVsRunningDocumentTable.GetDocumentInfo"/>: the latter always materialises the doc
/// data, creating it if necessary, which would initialize the very stubs we are trying to observe.
/// See <see href="https://learn.microsoft.com/visualstudio/extensibility/internals/delayed-document-loading">Delayed document loading</see>.
/// </para>
/// <para>All members must be called on the UI thread.</para>
/// </remarks>
internal sealed class DocumentInitializationMonitor : IVsRunningDocTableEvents2, IDisposable
{
    private readonly IVsRunningDocumentTable _rdt;
    private readonly IIdeSupportLogger _logger;
    private readonly Stopwatch _sinceAdvise;
    private uint _cookie;
    private bool _disposed;

    private DocumentInitializationMonitor(IVsRunningDocumentTable rdt, IIdeSupportLogger logger)
    {
        _rdt = rdt;
        _logger = logger;
        _sinceAdvise = Stopwatch.StartNew();
    }

    /// <summary>
    /// Subscribes to RDT events and logs the current document inventory. Returns
    /// <see langword="null"/> if the RDT is unavailable or the subscription fails — this is a
    /// diagnostic aid and must never break package initialization.
    /// </summary>
    public static DocumentInitializationMonitor? TryAdvise(
        IVsRunningDocumentTable? rdt,
        IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (rdt is null)
        {
            logger.LogInfo("DocumentInitializationMonitor: RDT unavailable; not advising.");
            return null;
        }

        var monitor = new DocumentInitializationMonitor(rdt, logger);
        try
        {
            rdt.AdviseRunningDocTableEvents(monitor, out monitor._cookie);
        }
        catch (Exception ex)
        {
            logger.LogException(ex, "DocumentInitializationMonitor: AdviseRunningDocTableEvents failed.");
            return null;
        }

        monitor.LogDocumentInventory();
        return monitor;
    }

    /// <summary>
    /// Logs every document currently in the RDT and whether it is still a stub: per-document
    /// detail for the kinds that can trigger activation (<c>.feature</c>, <c>.cs</c>), and totals
    /// for everything else.
    /// </summary>
    private void LogDocumentInventory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_rdt is not IVsRunningDocumentTable4 rdt4)
        {
            _logger.LogInfo("DocumentInitializationMonitor: RDT does not implement IVsRunningDocumentTable4; skipping inventory.");
            return;
        }

        try
        {
            _rdt.GetRunningDocumentsEnum(out var docs);
            if (docs is null)
                return;

            var cookies = new uint[1];
            int total = 0, totalStubs = 0, featureStubs = 0, csharpStubs = 0, features = 0, csharpFiles = 0;

            while (docs.Next(1, cookies, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                var cookie = cookies[0];

                // Both calls are stub-safe: neither creates the doc data.
                var moniker = rdt4.GetDocumentMoniker(cookie);
                var flags = (_VSRDTFLAGS4)rdt4.GetDocumentFlags(cookie);
                var pending = (flags & _VSRDTFLAGS4.RDT_PendingInitialization) != 0;
                var pendingHierarchy = (flags & _VSRDTFLAGS4.RDT_PendingHierarchyInitialization) != 0;

                total++;
                if (pending)
                    totalStubs++;

                switch (RdtDocumentInitialization.Classify(moniker))
                {
                    case RdtDocumentKind.Feature:
                        features++;
                        if (pending) featureStubs++;
                        break;
                    case RdtDocumentKind.CSharp:
                        csharpFiles++;
                        if (pending) csharpStubs++;
                        break;
                    default:
                        continue; // counted above; no per-document line for unrelated files
                }

                _logger.LogInfo(
                    $"DocumentInitializationMonitor: {moniker} — {(pending ? "STUB (pending initialization)" : "initialized")}" +
                    $"{(pendingHierarchy ? ", hierarchy pending" : string.Empty)}.");
            }

            _logger.LogInfo(
                $"DocumentInitializationMonitor: inventory at advise time — {total} document(s), {totalStubs} stub(s); " +
                $".feature: {features} ({featureStubs} stub), .cs: {csharpFiles} ({csharpStubs} stub).");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, "DocumentInitializationMonitor: inventory scan failed.");
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

        if (RdtDocumentInitialization.IsDocumentInitialization(moniker, grfAttribs)
            && RdtDocumentInitialization.IsActivationRelevant(moniker))
        {
            _logger.LogInfo(
                $"DocumentInitializationMonitor: {moniker} — initialized at +{_sinceAdvise.ElapsedMilliseconds}ms.");
        }

        return VSConstants.S_OK;
    }

    /// <inheritdoc />
    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

    /// <summary>Logs the first lock taken on a document — the closest RDT signal to "this document was opened".</summary>
    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
    {
        LogDocumentEvent(docCookie, "first document lock", $"lockType=0x{dwRDTLockType:X}");
        return VSConstants.S_OK;
    }

    /// <summary>Logs the last lock being released — the closest RDT signal to "this document was closed".</summary>
    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
    {
        LogDocumentEvent(docCookie, "last document unlock", $"lockType=0x{dwRDTLockType:X}");
        return VSConstants.S_OK;
    }

    /// <inheritdoc />
    public int OnAfterSave(uint docCookie) => VSConstants.S_OK;

    /// <summary>
    /// Logs <em>every</em> document window show, first or not.
    /// </summary>
    /// <remarks>
    /// Originally first-show only, which turned out to be unreadable: on a cold start the restored
    /// tab's first show happens before this monitor is advised, so the only line that ever appeared
    /// was for a document the user closed and reopened — easy to mistake for the user clicking the
    /// restored tab. Logging every show makes a plain tab click visible as such, which is what
    /// distinguishes "focus/show re-triggers activation" from "only a fresh document open does".
    /// </remarks>
    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        LogDocumentEvent(docCookie, "window show", $"firstShow={fFirstShow != 0}");
        return VSConstants.S_OK;
    }

    /// <summary>Logs a document window being hidden — the other half of a tab switch.</summary>
    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
    {
        LogDocumentEvent(docCookie, "window hide", null);
        return VSConstants.S_OK;
    }

    /// <summary>
    /// Logs one RDT event for a document, timestamped relative to package load, filtered to the
    /// document kinds that can activate the language server provider so the log stays readable.
    /// </summary>
    private void LogDocumentEvent(uint docCookie, string what, string? detail)
    {
        try
        {
            if (_rdt is not IVsRunningDocumentTable4 rdt4)
                return;

            // Stub-safe: GetDocumentMoniker does not create the doc data.
            var moniker = rdt4.GetDocumentMoniker(docCookie);
            if (!RdtDocumentInitialization.IsActivationRelevant(moniker))
                return;

            _logger.LogInfo(
                $"DocumentInitializationMonitor: {moniker} — {what}" +
                $"{(detail is null ? string.Empty : $" ({detail})")} " +
                $"at +{_sinceAdvise.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"DocumentInitializationMonitor: logging '{what}' failed.");
        }
    }

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
            _logger.LogException(ex, "DocumentInitializationMonitor: UnadviseRunningDocTableEvents failed.");
        }
        finally
        {
            _cookie = 0;
        }
    }
}
