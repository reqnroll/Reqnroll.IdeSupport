using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Intercepts the built-in Format Document / Format Selection commands for <c>Gherkin</c> text views
/// and redirects them to the Reqnroll LSP server via <see cref="FormatDocumentRedirect"/>.
/// </summary>
/// <remarks>
/// This is a MEF component registered via <c>[Export(typeof(IVsTextViewCreationListener))]</c>
/// with a <c>[ContentType("Gherkin")]</c> and <c>[TextViewRole(PredefinedTextViewRoles.Editable)]</c>
/// constraint so it only intercepts editable .feature file text views.
///
/// VS's out-of-process <c>LanguageServerProvider</c> model does not route the native
/// <c>Edit.FormatDocument</c>/<c>Edit.FormatSelection</c> commands to the standard
/// <c>textDocument/formatting</c>/<c>rangeFormatting</c> LSP requests on its own — the same gap
/// already worked around for Comment/Uncomment by <see cref="CommentToggleCommandFilter"/>. This
/// filter consumes the command, sends the request itself, and applies the returned edit(s) to the
/// text buffer directly (unlike Comment/Uncomment and Rename, formatting is a plain request/response —
/// the server does not push a <c>workspace/applyEdit</c> for it).
/// </remarks>
public sealed class FormatDocumentCommandFilter : IOleCommandTarget
{
    /// <summary>
    /// MEF export: creates a <see cref="FormatDocumentCommandFilter"/> for each
    /// <see cref="IVsTextView"/> whose content type is <c>Gherkin</c>.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("Gherkin")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class TextViewCreationListener : IVsTextViewCreationListener
    {
        private readonly IVsEditorAdaptersFactoryService _editorAdapter;
        private readonly IIdeSupportLogger _logger;

        /// <summary>
        /// MEF importing constructor.
        /// </summary>
        [ImportingConstructor]
        public TextViewCreationListener(IVsEditorAdaptersFactoryService editorAdapter, IIdeSupportLogger logger)
        {
            _editorAdapter = editorAdapter;
            _logger = logger;
        }

        /// <summary>
        /// Installs a <see cref="FormatDocumentCommandFilter"/> in the command chain for the newly
        /// created <paramref name="textViewAdapter"/>.
        /// </summary>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            // Do NOT gate on GetWpfTextView here — see CommentToggleCommandFilter's identical
            // remark: in the VS.Extensibility hybrid model the IVsTextView COM adapter can be
            // created before its WPF wrapper is linked into the adapter factory. The WPF view is
            // resolved lazily in Exec instead.
            var filter = new FormatDocumentCommandFilter(textViewAdapter, _editorAdapter, _logger);

            textViewAdapter.AddCommandFilter(filter, out var nextTarget);
            filter._nextCommandTarget = nextTarget;
        }
    }

    // ── Instance ──────────────────────────────────────────────────────────

    // Command set GUID for the standard editor commands.
    // VSConstants.GUID_VSStd2K = {1496A755-94DE-11D0-8C3F-00C04FC2AAE2}
    private static readonly Guid CommandSet = new("{1496A755-94DE-11D0-8C3F-00C04FC2AAE2}");

    private const uint CmdIdFormatDocument  = (uint)VSConstants.VSStd2KCmdID.FORMATDOCUMENT;
    private const uint CmdIdFormatSelection = (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION;

    private readonly IVsTextView _vsTextView;
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly IIdeSupportLogger _logger;

    // Resolved on first Exec call; null until then.
    private IWpfTextView? _wpfTextView;

    private IOleCommandTarget? _nextCommandTarget;

    internal FormatDocumentCommandFilter(IVsTextView vsTextView, IVsEditorAdaptersFactoryService editorAdapter, IIdeSupportLogger logger)
    {
        _vsTextView    = vsTextView;
        _editorAdapter = editorAdapter;
        _logger        = logger;
    }

    // ── IOleCommandTarget ─────────────────────────────────────────────────

    /// <summary>
    /// Intercepts Format Document/Format Selection commands and redirects them to the LSP
    /// server; unrecognized commands are forwarded to the next target in the chain.
    /// </summary>
    public int Exec(ref Guid commandGroup, uint commandId, uint executeOptions, IntPtr variantIn, IntPtr variantOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (commandGroup == CommandSet &&
            (commandId == CmdIdFormatDocument || commandId == CmdIdFormatSelection))
        {
            _wpfTextView ??= _editorAdapter.GetWpfTextView(_vsTextView);
            if (_wpfTextView is null)
            {
                _logger.LogInfo(
                    $"FormatDocumentCommandFilter: WPF view not available for command id={commandId}, ignoring.");
                return VSConstants.S_OK;
            }

            var redirect = FormatDocumentRedirect.FormatDocumentAsync;
            if (redirect is not null)
            {
                var wpfTextView = _wpfTextView;
                var fileUri     = GetTextBufferFileUri(wpfTextView);
                var isSelection = commandId == CmdIdFormatSelection;

                var selection  = wpfTextView.Selection;
                var startLine  = selection.Start.Position.GetContainingLine().LineNumber;
                var endLine    = selection.End.Position.GetContainingLine().LineNumber;

                _logger.LogInfo(
                    $"FormatDocumentCommandFilter: redirecting command id={commandId} uri='{fileUri}' isSelection={isSelection} lines[{startLine}..{endLine}]");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var edits = await redirect(fileUri, isSelection, startLine, endLine, CancellationToken.None)
                            .ConfigureAwait(false);

                        if (edits is null || edits.Count == 0)
                            return;

                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        ApplyEdits(wpfTextView.TextBuffer, edits);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(ex, "FormatDocumentCommandFilter: format failed");
                    }
                });
            }
            else
            {
                _logger.LogInfo(
                    $"FormatDocumentCommandFilter: redirect not available — ignoring command id={commandId}");
            }

            return VSConstants.S_OK;
        }

        // Forward unhandled commands to the next target in the chain.
        return _nextCommandTarget?.Exec(ref commandGroup, commandId, executeOptions, variantIn, variantOut)
               ?? VSConstants.E_FAIL;
    }

    /// <summary>
    /// Reports the Format Document/Format Selection commands as always enabled and supported;
    /// other commands are delegated to the next target in the chain.
    /// </summary>
    public int QueryStatus(ref Guid commandGroup, uint commandCount, OLECMD[] commands, IntPtr text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (commandGroup == CommandSet && commandCount > 0)
        {
            var cmdId = commands[0].cmdID;
            if (cmdId == CmdIdFormatDocument || cmdId == CmdIdFormatSelection)
            {
                commands[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                return VSConstants.S_OK;
            }
        }

        return _nextCommandTarget?.QueryStatus(ref commandGroup, commandCount, commands, text)
               ?? VSConstants.E_FAIL;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // internal rather than private so Reqnroll.IdeSupport.VisualStudio.Tests (an InternalsVisibleTo
    // friend assembly) can construct this filter directly to unit test ApplyEdits/GetTextBufferFileUri,
    // which need no real VS host.
    internal static void ApplyEdits(ITextBuffer textBuffer, IReadOnlyList<GherkinLineRangeEdit> edits)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var snapshot = textBuffer.CurrentSnapshot;
        using var edit = textBuffer.CreateEdit();

        foreach (var e in edits)
        {
            if (e.StartLine < 0 || e.EndLine < e.StartLine || e.EndLine >= snapshot.LineCount)
                continue;

            var startLine = snapshot.GetLineFromLineNumber(e.StartLine);
            var endLine   = snapshot.GetLineFromLineNumber(e.EndLine);
            var span      = Span.FromBounds(startLine.Start.Position, endLine.End.Position);

            edit.Replace(span, e.NewText);
        }

        edit.Apply();
    }

    internal string GetTextBufferFileUri(IWpfTextView wpfTextView)
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
            _logger.LogWarning($"FormatDocumentCommandFilter: failed to get file URI: {ex.Message}");
        }

        return string.Empty;
    }
}
