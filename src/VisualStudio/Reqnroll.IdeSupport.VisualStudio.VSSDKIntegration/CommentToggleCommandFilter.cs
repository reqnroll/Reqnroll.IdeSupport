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
/// Intercepts the built-in Comment Selection / Uncomment Selection / Toggle Line Comment
/// commands for <c>Gherkin</c> text views and redirects them to the Reqnroll
/// LSP server via <see cref="CommentToggleRedirect"/> (Comment/Uncomment toggle).
/// </summary>
/// <remarks>
/// This is a MEF component registered via <c>[Export(typeof(IVsTextViewCreationListener))]</c>
/// with a <c>[ContentType("Gherkin")]</c> and <c>[TextViewRole(PredefinedTextViewRoles.Editable)]</c>
/// constraint so it only intercepts editable .feature file text views.
///
/// When the user presses <c>Ctrl+K, Ctrl+C</c> (Comment Selection), <c>Ctrl+K, Ctrl+U</c>
/// (Uncomment Selection), or <c>Ctrl+/</c> (Toggle Line Comment) in a .feature file — or invokes
/// those commands any other way (Edit menu, toolbar, custom key binding) — this filter consumes
/// the command and sends a <c>workspace/executeCommand</c> request to the LSP server instead,
/// with the <see cref="CommentToggleMode"/> matching the command.
/// </remarks>
public sealed class CommentToggleCommandFilter : IOleCommandTarget
{
    /// <summary>
    /// MEF export: creates a <see cref="CommentToggleCommandFilter"/> for each
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
        /// Installs a <see cref="CommentToggleCommandFilter"/> in the command chain for the newly
        /// created <paramref name="textViewAdapter"/>.
        /// </summary>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            // Do NOT gate on GetWpfTextView here.  In the VS.Extensibility hybrid model
            // (RequiresInProcessHosting = true) the IVsTextView COM adapter can be created
            // before its WPF wrapper is linked into the adapter factory, so GetWpfTextView
            // returns null at this point and would cause a silent early-return that skips
            // AddCommandFilter entirely.  The WPF view is resolved lazily in Exec instead.
            var filter = new CommentToggleCommandFilter(textViewAdapter, _editorAdapter, _logger);

            // AddCommandFilter outputs the next target in the chain.
            // The filter intercepts comment commands before the default handler sees them.
            textViewAdapter.AddCommandFilter(filter, out var nextTarget);
            filter._nextCommandTarget = nextTarget;
        }
    }

    // ── Instance ──────────────────────────────────────────────────────────

    // Edit.ToggleLineComment is not in VSStd2K and has no VSConstants entry: it belongs to the
    // editor's own command set. Found in the CommandBindings of VS's
    // Microsoft.VisualStudio.Editor.Implementation.dll, which maps
    // {160961B3-909D-4B28-9353-A1BEF587B4A6}:48 to ToggleLineCommentCommandArgs (issue #747).
    internal static readonly Guid EditorCommandSet = new("{160961B3-909D-4B28-9353-A1BEF587B4A6}");
    internal const uint CmdIdToggleLineComment = 48;

    private readonly IVsTextView                     _vsTextView;
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly IIdeSupportLogger                 _logger;

    // Resolved on first Exec call; null until then.
    private IWpfTextView? _wpfTextView;

    private IOleCommandTarget? _nextCommandTarget;

    // internal rather than private so Reqnroll.IdeSupport.VisualStudio.Tests (an InternalsVisibleTo
    // friend assembly — see the VSSDKIntegration csproj) can construct this filter directly to unit
    // test GetTextBufferFileUri below, which needs no real VS host.
    internal CommentToggleCommandFilter(IVsTextView vsTextView, IVsEditorAdaptersFactoryService editorAdapter, IIdeSupportLogger logger)
    {
        _vsTextView    = vsTextView;
        _editorAdapter = editorAdapter;
        _logger        = logger;
    }

    // ── IOleCommandTarget ─────────────────────────────────────────────────

    /// <summary>
    /// Intercepts Comment/Uncomment/Toggle Line Comment commands and redirects them to the
    /// LSP server; unrecognized commands are forwarded to the next target in the chain.
    /// </summary>
    public int Exec(ref Guid commandGroup, uint commandId, uint executeOptions, IntPtr variantIn, IntPtr variantOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryGetCommentMode(commandGroup, commandId, out var mode))
        {
            // Resolve the WPF view lazily — it may not have been available when the filter
            // was installed during VsTextViewCreated (hybrid VS.Extensibility model).
            _wpfTextView ??= _editorAdapter.GetWpfTextView(_vsTextView);
            if (_wpfTextView is null)
            {
                _logger.LogInfo(
                    $"CommentToggleCommandFilter: WPF view not available for command id={commandId}, ignoring.");
                return VSConstants.S_OK;
            }

            var redirect = CommentToggleRedirect.ToggleCommentAsync;
            if (redirect is not null)
            {
                var fileUri   = GetTextBufferFileUri(_wpfTextView);
                var selection = _wpfTextView.Selection;
                var endPos    = selection.End.Position;
                var endContainingLine = endPos.GetContainingLine();
                var startLine = selection.Start.Position.GetContainingLine().LineNumber;
                var endLine   = CommentToggleLineRange.AdjustEndLineForWholeLineSelection(
                    startLine, endContainingLine.LineNumber, endPos == endContainingLine.Start);

                _logger.LogInfo(
                    $"CommentToggleCommandFilter: redirecting command id={commandId} mode={mode} uri='{fileUri}' lines[{startLine}..{endLine}]");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await redirect(fileUri, startLine, endLine, mode, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(ex, "CommentToggleCommandFilter: redirect failed");
                    }
                });
            }
            else
            {
                _logger.LogInfo(
                    $"CommentToggleCommandFilter: redirect not available — ignoring command id={commandId}");
            }

            return VSConstants.S_OK;
        }

        // Forward unhandled commands to the next target in the chain.
        return _nextCommandTarget?.Exec(ref commandGroup, commandId, executeOptions, variantIn, variantOut)
               ?? VSConstants.E_FAIL;
    }

    /// <summary>
    /// Reports the Comment/Uncomment/Toggle Line Comment commands as always enabled and
    /// supported; other commands are delegated to the next target in the chain.
    /// </summary>
    public int QueryStatus(ref Guid commandGroup, uint commandCount, OLECMD[] commands, IntPtr text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (commandCount > 0 && TryGetCommentMode(commandGroup, commands[0].cmdID, out _))
        {
            // Always supported.
            commands[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
            return VSConstants.S_OK;
        }

        return _nextCommandTarget?.QueryStatus(ref commandGroup, commandCount, commands, text)
               ?? VSConstants.E_FAIL;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a built-in comment command to the <see cref="CommentToggleMode"/> it asks for; returns
    /// <see langword="false"/> for every other command.
    /// </summary>
    /// <remarks>
    /// Kept pure (no <c>ThreadHelper</c>) so the command IDs can be unit tested — hard-coded wrong
    /// IDs once left this filter silently intercepting nothing (issue #747). Both VSStd2K spellings
    /// of Comment/Uncomment Selection are matched: <c>COMMENT_BLOCK</c>/<c>UNCOMMENT_BLOCK</c> is
    /// what <c>Edit.CommentSelection</c>/<c>Edit.UncommentSelection</c> dispatch today, and the
    /// older <c>COMMENTBLOCK</c>/<c>UNCOMMENTBLOCK</c> are still routed by some callers.
    /// </remarks>
    internal static bool TryGetCommentMode(Guid commandGroup, uint commandId, out CommentToggleMode mode)
    {
        if (commandGroup == VSConstants.VSStd2K)
        {
            switch ((VSConstants.VSStd2KCmdID)commandId)
            {
                case VSConstants.VSStd2KCmdID.COMMENT_BLOCK:
                case VSConstants.VSStd2KCmdID.COMMENTBLOCK:
                    mode = CommentToggleMode.Comment;
                    return true;
                case VSConstants.VSStd2KCmdID.UNCOMMENT_BLOCK:
                case VSConstants.VSStd2KCmdID.UNCOMMENTBLOCK:
                    mode = CommentToggleMode.Uncomment;
                    return true;
            }
        }
        else if (commandGroup == EditorCommandSet && commandId == CmdIdToggleLineComment)
        {
            mode = CommentToggleMode.Toggle;
            return true;
        }

        mode = default;
        return false;
    }

    // internal rather than private so it can be unit tested directly (see the constructor's note above) —
    // unlike Exec/QueryStatus it never touches ThreadHelper, so it doesn't need a real VS UI thread.
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
            _logger.LogWarning($"CommentToggleCommandFilter: failed to get file URI: {ex.Message}");
        }

        return string.Empty;
    }
}
