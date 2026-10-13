#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension.Documents;
using Reqnroll.IdeSupport.VisualStudio.Extension.Menus;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Surfaces 1 and 2 — "Find Step Usages" command placed in the Extensions menu and (E6-validated)
/// the C# editor context menu. Extracts the caret position from the active text view, delegates to
/// <see cref="FindStepUsagesService"/>, and renders results via <see cref="FindStepUsagesRenderer"/>.
/// </summary>
[VisualStudioContribution]
internal sealed class FindStepUsagesCommand : Command
{
    private readonly FindStepUsagesState _state;
    private readonly ILogger<FindStepUsagesCommand> _logger;

    // Inject only the registered shared-state singleton + the shared ILogger<T> — both guaranteed
    // resolvable.  Do NOT inject ReqnrollLanguageClient: contribution classes are not documented
    // as injectable into other contributions, and an unresolvable ctor dependency makes the
    // framework fail command construction silently (menu item shows, click does nothing).
    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public FindStepUsagesCommand(FindStepUsagesState state, ILogger<FindStepUsagesCommand> logger)
    {
        _state  = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Find Step Usages")
    {
        // VS.Extensibility MenuConfiguration has no Icon property, so the icon is carried on the
        // command item itself.  It appears in both placements: the Reqnroll submenu (Surface 1) and
        // the C# editor context menu (Surface 2).
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a C# file editor is active; invisible in all other editors (including .feature files).
        VisibleWhen = ActivationConstraint.EditorContentType(CSharpDocumentType.CSharp),

        Placements =
        [
            // Surface 1 — child of the Reqnroll submenu in the Extensions menu (ReqnrollMenu.cs).

            // Surface 2 — C# editor context menu, in the built-in navigation group next to
            // "Find All References".  VisualStudio.Extensibility's VsctParent can target groups
            // defined by the shell directly, so it needs no .vsct file.
            CommandPlacement.VsctParent(
                ShellMenuIds.GuidSHLMainMenu, id: ShellMenuIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION, priority: 0x0100),
        ],
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("FindStepUsagesCommand: invoked.");

            var service  = _state.Service;
            var renderer = _state.Renderer;
            if (service is null || renderer is null)
            {
                _logger.LogWarning(
                    "FindStepUsagesCommand: LSP server not yet initialized (service={ServiceState}, renderer={RendererState}).",
                    service is null ? "null" : "set", renderer is null ? "null" : "set");
                await VsUtils.ShowStatusBarMessageAsync("Reqnroll: LSP server not yet initialized — open a .feature file to activate it.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _logger.LogWarning("FindStepUsagesCommand: no active text view in client context.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;                 // 0-based, matches LSP convention
            var charNum  = caretPos.Offset - line.Text.Start; // 0-based column

            _logger.LogDebug(
                "FindStepUsagesCommand: active view uri={FileUri}, caret line={LineNum} char={CharNum}.", fileUri, lineNum, charNum);

            var result = await service.FindUsagesAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsBinding)
            {
                _logger.LogInformation(
                    "FindStepUsagesCommand: caret is not on a binding at {FileUri}:{LineNum} — nothing to show.", fileUri, lineNum);
                await VsUtils.ShowStatusBarMessageAsync("Reqnroll: The caret is not on a step definition binding.");
                return;
            }

            var count = result.Locations.Count;
            var label = count == 0
                ? "0 usages"
                : $"{count} usage{(count == 1 ? "" : "s")} of step definition";

            _logger.LogInformation(
                "FindStepUsagesCommand: binding resolved with {UsageCount} usage(s); rendering {Label}.", count, label);

            await renderer.RenderAsync(label, result, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("FindStepUsagesCommand: render complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindStepUsagesCommand: failed.");
        }
    }
}
