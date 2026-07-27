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
/// commands for <c>reqnroll-gherkin</c> text views and redirects them to the Reqnroll
/// LSP server via <see cref="CommentToggleRedirect"/> (Comment/Uncomment toggle).
/// </summary>
/// <remarks>
/// This is a MEF component registered via <c>[Export(typeof(IVsTextViewCreationListener))]</c>
/// with a <c>[ContentType("reqnroll-gherkin")]</c> and <c>[TextViewRole(PredefinedTextViewRoles.Editable)]</c>
/// constraint so it only intercepts editable .feature file text views.
///
/// When the user presses <c>Ctrl+K, Ctrl+C</c> (Comment Selection), <c>Ctrl+K, Ctrl+U</c>
/// (Uncomment Selection), or <c>Ctrl+/</c> (Toggle Line Comment) in a .feature file, this
/// filter consumes the command and sends a <c>workspace/executeCommand</c> request to the
/// LSP server instead.
/// </remarks>
public sealed class CommentToggleCommandFilter : IOleCommandTarget
{
    /// <summary>
    /// MEF export: creates a <see cref="CommentToggleCommandFilter"/> for each
    /// <see cref="IVsTextView"/> whose content type is <c>reqnroll-gherkin</c>.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("reqnroll-gherkin")]
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

    // Command set GUID for the comment commands.
    // VSConstants.GUID_VSStd2K = {1496A755-94DE-11D0-8C3F-00C04FC2AAE2}
    private static readonly Guid CommandSet = new("{1496A755-94DE-11D0-8C3F-00C04FC2AAE2}");

    private const uint CmdIdCommentBlock       = 145; // Edit.CommentSelection
    private const uint CmdIdUncommentBlock     = 146; // Edit.UncommentSelection
    private const uint CmdIdToggleLineComment  = 147; // Edit.ToggleLineComment

    private readonly IVsTextView                     _vsTextView;
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly IIdeSupportLogger                 _logger;

    // Resolved on first Exec call; null until then.
    private IWpfTextView? _wpfTextView;

    private IOleCommandTarget? _nextCommandTarget;

    private CommentToggleCommandFilter(IVsTextView vsTextView, IVsEditorAdaptersFactoryService editorAdapter, IIdeSupportLogger logger)
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

        if (commandGroup == CommandSet &&
            (commandId == CmdIdCommentBlock || commandId == CmdIdUncommentBlock || commandId == CmdIdToggleLineComment))
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
                    $"CommentToggleCommandFilter: redirecting command id={commandId} uri='{fileUri}' lines[{startLine}..{endLine}]");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await redirect(fileUri, startLine, endLine, CancellationToken.None)
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

        if (commandGroup == CommandSet && commandCount > 0)
        {
            var cmdId = commands[0].cmdID;
            if (cmdId == CmdIdCommentBlock || cmdId == CmdIdUncommentBlock || cmdId == CmdIdToggleLineComment)
            {
                // Always supported.
                commands[0].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                return VSConstants.S_OK;
            }
        }

        return _nextCommandTarget?.QueryStatus(ref commandGroup, commandCount, commands, text)
               ?? VSConstants.E_FAIL;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
            _logger.LogWarning($"CommentToggleCommandFilter: failed to get file URI: {ex.Message}");
        }

        return string.Empty;
    }
}
