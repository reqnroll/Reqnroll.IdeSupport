using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.NavigationBar;

/// <summary>
/// Creates a <see cref="GherkinDropdownBarClient"/> Navigation Bar drop-down for each
/// <c>gherkin</c> text view, without owning a legacy
/// <see cref="IVsLanguageInfo"/> language service — the client itself reaches
/// <see cref="IVsDropdownBarManager"/> via the view's <see cref="IVsWindowFrame"/> and attaches
/// directly. All resolution/attach logic (and its retries) lives on the client, not here — this
/// listener just supplies the COM adapter and its dependencies.
/// </summary>
/// <remarks>
/// Normally a code window's drop-down bar is owned by whichever <see cref="IVsLanguageInfo"/>
/// is registered for the file (via <c>GetCodeWindowManager</c>) — this repo has none, by design,
/// since it's LSP-based. This bypasses that ownership model; confirmed working in a running VS
/// instance before the real client (symbol-backed combos) was built.
/// </remarks>
[Export(typeof(IVsTextViewCreationListener))]
[ContentType("gherkin")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class GherkinDropdownBarTextViewCreationListener : IVsTextViewCreationListener
{
    private readonly IVsEditorAdaptersFactoryService _editorAdapter;
    private readonly SVsServiceProvider _serviceProvider;
    private readonly IIdeSupportLogger _logger;

    [ImportingConstructor]
    public GherkinDropdownBarTextViewCreationListener(
        IVsEditorAdaptersFactoryService editorAdapter,
        SVsServiceProvider serviceProvider,
        IIdeSupportLogger logger)
    {
        _editorAdapter = editorAdapter;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (_serviceProvider.GetService(typeof(SVsUIShell)) is not IVsUIShell uiShell)
            {
                _logger.LogWarning("GherkinDropdownBarTextViewCreationListener: SVsUIShell unavailable.");
                return;
            }

            // The client itself resolves the window frame/dropdown-bar manager/WPF view/file URI
            // lazily (with retries) — none of them are reliably available yet at this point,
            // especially for .feature files restored on solution open.
            _ = new GherkinDropdownBarClient(textViewAdapter, _editorAdapter, uiShell, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"GherkinDropdownBarTextViewCreationListener: failed to create client: {ex}");
        }
    }
}
