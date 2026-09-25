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
/// Intercepts the built-in Go To Definition command (F12) for <c>Gherkin</c> text views and
/// redirects it to the Reqnroll LSP server via <see cref="GoToDefinitionRedirect"/> (issue #757).
/// </summary>
/// <remarks>
/// VS's own LSP client handles Go To Definition correctly, but when a step has several matching
/// step definitions it opens the Find All References window titled <c>'{word}' declarations</c>,
/// where <c>{word}</c> is the single text-navigator word at the caret (confirmed by decompiling
/// <c>RemoteReferencesBroker.GetDefinitionTextAsync</c> in
/// <c>Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll</c>) — e.g. <c>'50' declarations</c>
/// for a caret on the <c>50</c> in <c>Given the first number is 50</c>. Nothing the server returns
/// can change that title, so this filter takes the command over and the Extension side presents the
/// results with a title naming the whole step.
/// <para>
/// Only the command is intercepted; Ctrl+Click goes through VS's navigable-symbol path and still
/// shows the one-word title (deliberately left as a follow-up). When the redirect is not set (server
/// not yet initialized) the command is forwarded unchanged so VS's own handler still runs.
/// </para>
/// </remarks>
public sealed class GoToDefinitionCommandFilter : IOleCommandTarget
{
    /// <summary>
    /// MEF export: creates a <see cref="GoToDefinitionCommandFilter"/> for each
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
        /// Installs a <see cref="GoToDefinitionCommandFilter"/> in the command chain for the newly
        /// created <paramref name="textViewAdapter"/>.
        /// </summary>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            // Do NOT gate on GetWpfTextView here — see CommentToggleCommandFilter's identical
            // remark: in the VS.Extensibility hybrid model the IVsTextView COM adapter can be
            // created before its WPF wrapper is linked into the adapter factory. The WPF view is
            // resolved lazily in Exec instead.
            var filter = new GoToDefinitionCommandFilter(textViewAdapter, _editorAdapter, _logger);

            textViewAdapter.AddCommandFilter(filter, out var nextTarget);
            filter._nextCommandTarget = nextTarget;
        }
    }

    // ── Instance ──────────────────────────────────────────────────────────

    // Edit.GoToDefinition: VSConstants.GUID_VSStandardCommandSet97 / VSStd97CmdID.GotoDefn (935).
    private static readonly Guid CommandSet = VSConstants.GUID_VSStandardCommandSet97;
    private const uint CmdIdGoToDefinition = (uint)VSConstants.VSStd97CmdID.GotoDefn;

    private readonly IVsTextView _vsTextView;
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly IIdeSupportLogger _logger;

    // Resolved on first Exec call; null until then.
    private IWpfTextView? _wpfTextView;

    private IOleCommandTarget? _nextCommandTarget;

    // internal rather than private so Reqnroll.IdeSupport.VisualStudio.Tests (an InternalsVisibleTo
    // friend assembly) can construct this filter directly to unit test GetTextBufferFileUri, which
    // needs no real VS host.
    internal GoToDefinitionCommandFilter(IVsTextView vsTextView, IVsEditorAdaptersFactoryService editorAdapter, IIdeSupportLogger logger)
    {
        _vsTextView    = vsTextView;
        _editorAdapter = editorAdapter;
        _logger        = logger;
    }

    // ── IOleCommandTarget ─────────────────────────────────────────────────

    /// <summary>
    /// Intercepts Go To Definition and redirects it to the LSP server; unrecognized commands, and
    /// Go To Definition itself while no redirect is set, are forwarded to the next target in the chain.
    /// </summary>
    public int Exec(ref Guid commandGroup, uint commandId, uint executeOptions, IntPtr variantIn, IntPtr variantOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var redirect = GoToDefinitionRedirect.GoToDefinitionAsync;
        var isGoToDefinition = commandGroup == CommandSet && commandId == CmdIdGoToDefinition;

        // Say why VS's own "'{word}' declarations" window may appear instead of ours (issue #757).
        if (isGoToDefinition && redirect is null)
            _logger.LogVerbose(
                "GoToDefinitionCommandFilter: LSP server not initialized (no redirect set), forwarding Go To Definition to VS.");

        if (isGoToDefinition && redirect is not null)
        {
            _wpfTextView ??= _editorAdapter.GetWpfTextView(_vsTextView);
            var fileUri = _wpfTextView is null ? string.Empty : GetTextBufferFileUri(_wpfTextView);

            // Without a file URI there is nothing to ask the server about; consuming the command
            // anyway would make F12 silently do nothing, so let VS's own handler have it.
            if (_wpfTextView is not null && fileUri.Length > 0)
            {
                var caret    = _wpfTextView.Caret.Position.BufferPosition;
                var line     = caret.GetContainingLine();
                var lineText = line.GetText();
                var line0    = line.LineNumber;
                var char0    = caret.Position - line.Start.Position;

                _logger.LogVerbose(
                    $"GoToDefinitionCommandFilter: redirecting Go To Definition uri='{fileUri}' at {line0}:{char0}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await redirect(fileUri, line0, char0, lineText, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogException(ex, "GoToDefinitionCommandFilter: Go To Definition failed");
                    }
                });

                return VSConstants.S_OK;
            }

            _logger.LogInfo("GoToDefinitionCommandFilter: no WPF view or file URI, forwarding Go To Definition.");
        }

        return _nextCommandTarget?.Exec(ref commandGroup, commandId, executeOptions, variantIn, variantOut)
               ?? VSConstants.E_FAIL;
    }

    /// <summary>
    /// Delegates entirely to the next target: VS's LSP client already reports Go To Definition as
    /// available for .feature files (the server registers <c>textDocument/definition</c>).
    /// </summary>
    public int QueryStatus(ref Guid commandGroup, uint commandCount, OLECMD[] commands, IntPtr text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return _nextCommandTarget?.QueryStatus(ref commandGroup, commandCount, commands, text)
               ?? VSConstants.E_FAIL;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
            _logger.LogWarning($"GoToDefinitionCommandFilter: failed to get file URI: {ex.Message}");
        }

        return string.Empty;
    }
}
