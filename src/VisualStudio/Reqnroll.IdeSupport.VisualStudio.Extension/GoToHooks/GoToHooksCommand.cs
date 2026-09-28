using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
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
    /// <summary>
    /// Telemetry event name for a genuine "Go to Hooks" navigation (issue #698). Originated here,
    /// client-side, rather than by the LSP server's <c>reqnroll/findHooks</c> handler: that handler
    /// also backs the classic VS CodeLens's Details-popup prefetch (every lens render, not just a
    /// click), so it cannot honestly claim every request is a navigation — only this command's own
    /// invocation genuinely is one. The server instead reports <c>TelemetryEvents.FindHooksCommandExecuted</c>
    /// for every request, prefetch or not.
    /// </summary>
    private const string GoToHookCommandExecutedEventName = "GoToHook command executed";

    private readonly FindHooksState  _state;
    private readonly LspServerConnectionService _connectionService;
    private readonly ILogger<GoToHooksCommand> _logger;
    // NavigationPickerHelper (shared with FindStepUsages/RenameStep-adjacent navigation code,
    // out of scope for the ILogger<T> migration) still takes IIdeSupportLogger — resolve the
    // shared DI-registered singleton sink for that one call rather than a second ad hoc logger.
    private readonly IIdeSupportLogger _fileLogger;

    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public GoToHooksCommand(
        FindHooksState              state,
        LspServerConnectionService  connectionService,
        ILogger<GoToHooksCommand>   logger,
        IIdeSupportLogger           fileLogger)
    {
        _state             = state;
        _connectionService = connectionService;
        _logger            = logger;
        _fileLogger        = fileLogger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Go to Hooks")
    {
        Icon        = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a .feature file editor is active; invisible in all other editors.
        VisibleWhen = ActivationConstraint.EditorContentType(VsWellKnownIds.GherkinContentType),

        // Placed in the navigation group of the code-editor context menu alongside
        // "Go To Definition" and "Find All References".
        Placements  =
        [
            CommandPlacement.VsctParent(ShellMenuIds.GuidSHLMainMenu, ShellMenuIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION, 0x0200),
        ],
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("GoToHooksCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _logger.LogWarning("GoToHooksCommand: LSP server not yet initialized.");
                return;
            }

            // A genuine navigation, as opposed to the classic CodeLens's Details-popup prefetch
            // (which never runs through this command) — emit here, not from the server's
            // reqnroll/findHooks handler, which cannot tell the two apart (issue #698).
            _connectionService.TelemetryTransmitter?.TransmitEvent(
                new GenericEvent(GoToHookCommandExecutedEventName, []));

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

            _logger.LogDebug(
                "GoToHooksCommand: uri={FileUri}, caret line={LineNum} char={CharNum}.", fileUri, lineNum, charNum);

            var result = await service
                .FindHooksAsync(fileUri, lineNum, charNum, cancellationToken)
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
