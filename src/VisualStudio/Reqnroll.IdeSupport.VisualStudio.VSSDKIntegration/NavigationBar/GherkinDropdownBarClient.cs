using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.NavigationBar;

/// <summary>
/// Navigation Bar drop-down client for <c>.feature</c> files: a single
/// combo listing Feature/Rule/Background/Scenario/ScenarioOutline titles (Steps/Examples omitted —
/// scenarios are short enough that the scenario title is almost always the intended navigation
/// target), sourced from the standard <c>textDocument/documentSymbol</c> data via
/// <see cref="NavigationBarRedirect"/>.
/// </summary>
/// <remarks>
/// Owns the entire lazy-resolution dance, not just the LSP fetch: both the <see cref="IVsWindowFrame"/>
/// (needed to reach <see cref="IVsDropdownBarManager"/>) and the <see cref="IWpfTextView"/> (needed
/// for caret tracking/navigation) can be unavailable at <c>VsTextViewCreated</c> time — most visibly
/// when a solution is opened with <c>.feature</c> files already restored from a previous session,
/// where many views are created back-to-back before VS finishes wiring up their WPF/frame plumbing.
/// A single missed resolution used to mean the drop-down bar for that view was silently never
/// attached at all. Every step below (attach, then LSP fetch) retries on the same 300ms debounce
/// timer until it succeeds, rather than giving up after one failed attempt.
/// </remarks>
internal sealed class GherkinDropdownBarClient : IVsDropdownBarClient, IDisposable
{
    private const int StructureCombo = 0;

    private readonly IVsTextView    _vsTextView;
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly IVsUIShell     _uiShell;
    private readonly IIdeSupportLogger _logger;
    private readonly DispatcherTimer _refreshTimer;

    private IVsDropdownBarManager? _dropdownBarManager;
    private IVsDropdownBar?        _dropdownBar;
    private IWpfTextView?          _wpfView;
    private string?                _fileUri;
    private bool                   _eventsHooked;
    private bool                   _disposed;

    private IReadOnlyList<GherkinSymbolNode> _roots            = Array.Empty<GherkinSymbolNode>();
    private IReadOnlyList<GherkinSymbolNode> _structureEntries = Array.Empty<GherkinSymbolNode>();

    // Issue #83: a successful-but-empty fetch during the startup window must not be trusted as
    // final the way a genuinely-empty file would be — otherwise the combo can get stuck empty
    // until some unrelated event (caret move, buffer edit) happens to call ScheduleRefresh()
    // again. Bounded so a genuinely empty file still settles.
    //
    // What the empty result actually races against: NOT step-binding computation — the Nav Bar's
    // structural symbols (Feature/Rule/Scenario/Background titles) come from
    // DocumentSymbolHandler.GetSymbols, which only needs the document's own Gherkin tags
    // (GherkinDocumentTaggerService.ParseAsync) to exist; step-binding match data is a separate,
    // best-effort annotation the tagger skips gracefully when the project registry isn't ready,
    // not a precondition for structural tags. The real dependency is narrower: whether
    // textDocument/didOpen has been processed for this URI at all yet (ParseAsync runs
    // synchronously inside that handler). When a solution restores many .feature tabs at once,
    // this view's fetch can win the race against its own didOpen notification still sitting in
    // the server's queue behind everyone else's. The retry budget below is sized for that queue
    // depth, not for the much slower whole-project C# binding discovery some other symptoms
    // (e.g. hover-for-ambiguous-bindings) genuinely do wait on.
    private bool _hasSeenNonEmptyStructure;
    private int  _emptyResultRetriesRemaining = 15;

    public GherkinDropdownBarClient(
        IVsTextView vsTextView,
        IVsEditorAdaptersFactoryService editorAdapter,
        IVsUIShell uiShell,
        IIdeSupportLogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _vsTextView    = vsTextView;
        _editorAdapter = editorAdapter;
        _uiShell       = uiShell;
        _logger        = logger;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

        ScheduleRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;

        if (_eventsHooked && _wpfView is not null)
        {
            _wpfView.TextBuffer.Changed    -= OnBufferChanged;
            _wpfView.Caret.PositionChanged -= OnCaretPositionChanged;
            _wpfView.Closed                -= OnViewClosed;
        }
    }

    // ── Refresh scheduling ───────────────────────────────────────────────

    private void OnViewClosed(object? sender, EventArgs e) => Dispose();

    private void OnBufferChanged(object? sender, TextContentChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ScheduleRefresh();
    }

    private void OnCaretPositionChanged(object? sender, CaretPositionChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ScheduleRefresh();
    }

    /// <summary>
    /// Debounced: re-attempt attach/re-fetch symbols at most once per 300ms of activity (caret
    /// moves, buffer edits, or an unresolved dependency retrying itself), not once per keystroke.
    /// DispatcherTimer is thread-affine — callers off the UI thread (the exception path in
    /// <see cref="RefreshAsync"/>) must switch to it first.
    /// </summary>
    private void ScheduleRefresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
            return;

        if (_dropdownBarManager is null && !TryAttach())
        {
            if (!_disposed)
                ScheduleRefresh();
            return;
        }

        var fetch = NavigationBarRedirect.FetchDocumentSymbolsAsync;
        if (fetch is null)
        {
            _logger.LogVerbose("GherkinDropdownBarClient: server not connected yet, retrying in 300ms.");
            ScheduleRefresh();
            return;
        }

        try
        {
            var roots = await fetch(_fileUri!, CancellationToken.None).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed)
                return;

            _roots            = roots;
            _structureEntries = GherkinNavigationBarLayout.BuildStructureEntries(_roots);

            if (_structureEntries.Count > 0)
            {
                _hasSeenNonEmptyStructure = true;
            }
            else if (!_hasSeenNonEmptyStructure && _emptyResultRetriesRemaining > 0)
            {
                _emptyResultRetriesRemaining--;
                ApplyCaretPosition();
                _logger.LogVerbose(
                    $"GherkinDropdownBarClient: '{_fileUri}' fetched 0 structure entries before any non-empty " +
                    $"result — the server may not have processed this document's didOpen yet (e.g. queued " +
                    $"behind other restored tabs' own didOpen notifications), retrying " +
                    $"({_emptyResultRetriesRemaining} attempt(s) left).");
                ScheduleRefresh();
                return;
            }

            ApplyCaretPosition();

            _logger.LogVerbose(
                $"GherkinDropdownBarClient: refreshed '{_fileUri}' — {_structureEntries.Count} structure entrie(s).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"GherkinDropdownBarClient: refresh failed for '{_fileUri}': {ex}");

            // Transient (e.g. server mid-restart) — retry rather than leaving stale/empty combos.
            // The exception may have surfaced before the SwitchToMainThreadAsync above ran, so
            // switch explicitly rather than assuming we're still on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!_disposed)
                ScheduleRefresh();
        }
    }

    // ── Lazy attach ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the <see cref="IVsWindowFrame"/> → <see cref="IVsCodeWindow"/> →
    /// <see cref="IVsDropdownBarManager"/> chain, the <see cref="IWpfTextView"/>, and the file URI,
    /// then calls <see cref="IVsDropdownBarManager.AddDropdownBar"/>. Any of these can legitimately
    /// be unavailable this early (see remarks on the class) — returns false without side effects
    /// (beyond logging) so the caller retries on the next debounce tick.
    /// </summary>
    private bool TryAttach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var codeWindow = ResolveCodeWindow(_vsTextView);
        if (codeWindow is null)
        {
            _logger.LogVerbose("GherkinDropdownBarClient: could not resolve IVsCodeWindow yet, retrying.");
            return false;
        }

        if (codeWindow is not IVsDropdownBarManager dropdownBarManager)
        {
            _logger.LogWarning("GherkinDropdownBarClient: IVsCodeWindow does not implement IVsDropdownBarManager.");
            return false;
        }

        // Avoid E_UNEXPECTED from double-attach (e.g. secondary view of a split window) — the
        // primary view's client already owns this code window's drop-down bar, so this instance
        // (which will never itself be attached/SetDropdownBar'd) has nothing left to do.
        int hr = dropdownBarManager.GetDropdownBar(out var existingBar);
        if (hr == VSConstants.S_OK && existingBar is not null)
        {
            _logger.LogVerbose("GherkinDropdownBarClient: drop-down bar already attached, standing down.");
            Dispose();
            return false;
        }

        var wpfView = _editorAdapter.GetWpfTextView(_vsTextView);
        if (wpfView is null)
        {
            _logger.LogVerbose("GherkinDropdownBarClient: WPF view not available yet, retrying.");
            return false;
        }

        var fileUri = GetTextBufferFileUri(wpfView);
        if (string.IsNullOrEmpty(fileUri))
        {
            _logger.LogVerbose("GherkinDropdownBarClient: could not resolve file URI yet, retrying.");
            return false;
        }

        hr = dropdownBarManager.AddDropdownBar(1, this);
        if (ErrorHandler.Failed(hr))
        {
            _logger.LogWarning($"GherkinDropdownBarClient: AddDropdownBar failed, hr=0x{hr:X8}");
            return false;
        }

        _dropdownBarManager = dropdownBarManager;
        _wpfView            = wpfView;
        _fileUri            = fileUri;

        _wpfView.TextBuffer.Changed    += OnBufferChanged;
        _wpfView.Caret.PositionChanged += OnCaretPositionChanged;
        _wpfView.Closed                += OnViewClosed;
        _eventsHooked = true;

        _logger.LogInfo($"GherkinDropdownBarClient: drop-down bar attached successfully for '{fileUri}'.");
        return true;
    }

    private IVsCodeWindow? ResolveCodeWindow(IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var frame = FindWindowFrame(textViewAdapter);
        if (frame is null)
            return null;

        if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView)))
            return null;

        return docView as IVsCodeWindow;
    }

    /// <summary>
    /// Finds the <see cref="IVsWindowFrame"/> hosting <paramref name="textViewAdapter"/> by
    /// enumerating the running document table and matching on the primary/secondary view.
    /// </summary>
    private IVsWindowFrame? FindWindowFrame(IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ErrorHandler.Failed(_uiShell.GetDocumentWindowEnum(out var frameEnum)) || frameEnum is null)
            return null;

        var frames = new IVsWindowFrame[1];
        while (frameEnum.Next(1, frames, out uint fetched) == VSConstants.S_OK && fetched == 1)
        {
            var frame = frames[0];
            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView)))
                continue;

            if (docView is IVsCodeWindow codeWindow &&
                MatchesView(codeWindow, textViewAdapter))
            {
                return frame;
            }
        }

        return null;
    }

    private static bool MatchesView(IVsCodeWindow codeWindow, IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ErrorHandler.Succeeded(codeWindow.GetPrimaryView(out var primaryView)) &&
            ReferenceEquals(primaryView, textViewAdapter))
        {
            return true;
        }

        return ErrorHandler.Succeeded(codeWindow.GetSecondaryView(out var secondaryView)) &&
               ReferenceEquals(secondaryView, textViewAdapter);
    }

    private string GetTextBufferFileUri(IWpfTextView wpfTextView)
    {
        try
        {
            if (wpfTextView.TextBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument doc))
            {
                return new Uri(doc.FilePath).AbsoluteUri;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"GherkinDropdownBarClient: failed to get file URI: {ex.Message}");
        }

        return string.Empty;
    }

    // ── Combo data ───────────────────────────────────────────────────────

    private void ApplyCaretPosition()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_dropdownBar is null || _wpfView is null)
            return;

        int caretLine = _wpfView.Caret.Position.BufferPosition.GetContainingLine().LineNumber;

        int structureIndex = GherkinNavigationBarLayout.FindSelectedStructureIndex(_structureEntries, caretLine);
        _dropdownBar.RefreshCombo(StructureCombo, structureIndex);
    }

    // ── IVsDropdownBarClient ─────────────────────────────────────────────

    public int GetComboAttributes(int iCombo, out uint pcEntries, out uint puEntryType, out IntPtr phImageList)
    {
        pcEntries    = (uint)Entries(iCombo).Count;
        puEntryType  = (uint)DROPDOWNENTRYTYPE.ENTRY_TEXT;
        phImageList  = IntPtr.Zero;
        return VSConstants.S_OK;
    }

    public int GetEntryText(int iCombo, int iIndex, out string ppText)
    {
        var entries = Entries(iCombo);
        ppText = iIndex >= 0 && iIndex < entries.Count ? entries[iIndex].Name : string.Empty;
        return VSConstants.S_OK;
    }

    public int GetEntryAttributes(int iCombo, int iIndex, out uint pAttr)
    {
        pAttr = (uint)DROPDOWNFONTATTR.FONTATTR_PLAIN;
        return VSConstants.S_OK;
    }

    public int GetEntryImage(int iCombo, int iIndex, out int piImageIndex)
    {
        piImageIndex = -1;
        return VSConstants.S_OK;
    }

    public int OnComboGetFocus(int iCombo) => VSConstants.S_OK;

    public int GetComboTipText(int iCombo, out string ppText)
    {
        ppText = string.Empty;
        return VSConstants.S_OK;
    }

    public int OnItemSelected(int iCombo, int iIndex) => VSConstants.S_OK;

    public int OnItemChosen(int iCombo, int iIndex)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var entries = Entries(iCombo);
        if (iIndex < 0 || iIndex >= entries.Count)
        {
            _logger.LogVerbose($"GherkinDropdownBarClient: OnItemChosen combo={iCombo} index={iIndex} out of range ({entries.Count} entries).");
            return VSConstants.S_OK;
        }

        var node = entries[iIndex];
        _logger.LogInfo(
            $"GherkinDropdownBarClient: OnItemChosen combo={iCombo} index={iIndex} " +
            $"name='{node.Name}' selectionRange=({node.SelectionRange.Start.Line},{node.SelectionRange.Start.Character}).");
        NavigateTo(node);
        return VSConstants.S_OK;
    }

    public int SetDropdownBar(IVsDropdownBar pDropdownBar)
    {
        _dropdownBar = pDropdownBar;
        return VSConstants.S_OK;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private IReadOnlyList<GherkinSymbolNode> Entries(int iCombo) =>
        iCombo == StructureCombo ? _structureEntries : Array.Empty<GherkinSymbolNode>();

    private void NavigateTo(GherkinSymbolNode node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_wpfView is null)
            return;

        var snapshot = _wpfView.TextBuffer.CurrentSnapshot;
        int line = Math.Max(0, Math.Min(node.SelectionRange.Start.Line, snapshot.LineCount - 1));
        var textLine = snapshot.GetLineFromLineNumber(line);
        int position = Math.Min(textLine.Start.Position + node.SelectionRange.Start.Character, textLine.End.Position);

        var point = new SnapshotPoint(snapshot, position);
        _wpfView.Caret.MoveTo(point);
        _wpfView.ViewScroller.EnsureSpanVisible(new SnapshotSpan(point, 0));
        _wpfView.VisualElement.Focus();

        _logger.LogVerbose($"GherkinDropdownBarClient: NavigateTo moved caret to line={line} position={position}.");
    }
}
