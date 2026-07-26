using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// "Go to Hooks" command — placed in the code-editor context menu navigation group,
/// visible only when a <c>.feature</c> file editor is active (design doc: Hook Navigation).
/// </summary>
/// <remarks>
/// When invoked, queries the LSP server for hook bindings applicable at the caret position.
/// A single result navigates directly; multiple results show a picker via
/// <see cref="NavigationPickerHelper.PickAndNavigateAsync"/>.
/// </remarks>
[VisualStudioContribution]
internal sealed class GoToHooksCommand : Command
{
    private readonly GoToHooksState  _state;
    private readonly ILogger<GoToHooksCommand> _logger;
    // NavigationPickerHelper (shared with FindStepUsages/RenameStep-adjacent navigation code,
    // out of scope for the ILogger<T> migration) still takes IIdeSupportLogger — resolve the
    // shared DI-registered singleton sink for that one call rather than a second ad hoc logger.
    private readonly IIdeSupportLogger _fileLogger;

    // guidSHLMainMenu (vsshlids.h) — the VS shell's built-in command set.
    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");

    // IDG_VS_CODEWIN_NAVIGATETOLOCATION (vsshlids.h) — navigation group in the code-editor
    // context menu (IDM_VS_CTXT_CODEWIN) that hosts "Go To Definition" / "Find All References".
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public GoToHooksCommand(GoToHooksState state, ILogger<GoToHooksCommand> logger, IIdeSupportLogger fileLogger)
    {
        _state      = state;
        _logger     = logger;
        _fileLogger = fileLogger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Go to Hooks")
    {
        Icon        = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a .feature file editor is active; invisible in all other editors.
        VisibleWhen = ActivationConstraint.EditorContentType("reqnroll-gherkin"),

        // Placed in the navigation group of the code-editor context menu alongside
        // "Go To Definition" and "Find All References".
        Placements  =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, IDG_VS_CODEWIN_NAVIGATETOLOCATION, 0x0200),
        ],
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("GoToHooksCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _logger.LogWarning("GoToHooksCommand: LSP server not yet initialized.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _logger.LogWarning("GoToHooksCommand: no active text view.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;
            var charNum  = caretPos.Offset - line.Text.Start;

            _logger.LogInformation(
                "GoToHooksCommand: uri={FileUri}, caret line={LineNum} char={CharNum}.", fileUri, lineNum, charNum);

            var result = await service
                .GoToHooksAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (result.Hooks.Count == 0)
            {
                _logger.LogInformation("GoToHooksCommand: no applicable hooks at this position.");
                return;
            }

            _logger.LogInformation("GoToHooksCommand: {HookCount} hook(s) found.", result.Hooks.Count);

            var targets = BuildTargets(result.Hooks);
            await NavigationPickerHelper.PickAndNavigateAsync(
                    targets,
                    _fileLogger,
                    promptTitle: "Go to Hooks",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoToHooksCommand: failed.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<NavigationTarget> BuildTargets(IReadOnlyList<HookLocation> hooks)
    {
        var targets = new List<NavigationTarget>(hooks.Count);
        foreach (var h in hooks)
        {
            if (!Uri.TryCreate(h.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
                continue;

            var filePath    = uri.LocalPath;
            var fileName    = Path.GetFileName(filePath);
            // Display: "[BeforeScenario] SetUpDatabase (Hooks.cs:10)"  (1-based line for readability)
            var displayText = $"[{h.HookType}] {h.MethodName} ({fileName}:{h.StartLine + 1})";
            targets.Add(new NavigationTarget(displayText, filePath, h.StartLine, h.StartChar));
        }
        return targets;
    }
}
