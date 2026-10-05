using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;
using Reqnroll.IdeSupport.VisualStudio.Extension.Menus;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// "Go to Hooks" command — placed in the code-editor context menu navigation group,
/// visible only when a <c>.feature</c> file editor is active (design doc: Hook Navigation).
/// </summary>
/// <remarks>
/// When invoked, queries the LSP server for hook bindings applicable at the caret position.
/// A single result navigates directly. Several results are shown in the Find All References
/// window (issue #315) instead of the <see cref="NavigationPickerHelper"/>'s modal picker —
/// consistent with the other Go To Hooks entry points (code lens click, scenario-title context
/// menu) and with Go To Definition's ambiguous-step handling
/// (<c>GoToStepDefinitionPresenter</c>), both of which already use the FAR window for this case.
/// </remarks>
[VisualStudioContribution]
internal sealed class GoToHooksCommand : Command
{
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
                GoToHookTelemetry.CreateEvent(GoToHookSources.ContextMenu));

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

            if (result.Hooks.Count == 1)
            {
                var targets = BuildTargets(result.Hooks);
                await NavigationPickerHelper.PickAndNavigateAsync(
                        targets,
                        _fileLogger,
                        promptTitle: "Go to Hooks",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // Several applicable hooks: show them in the Find All References window rather than
            // NavigationPickerHelper's NavigationPickerDialog modal popup (issue #315).
            var renderer = _state.Renderer;
            if (renderer is null)
            {
                _logger.LogWarning("GoToHooksCommand: FindStepUsagesRenderer not available.");
                return;
            }

            var locations = HookLocationsMapper.BuildLocations(result.Hooks);
            var label     = $"Reqnroll: {locations.Count} hooks";
            await renderer.RenderAsync(label, new StepUsagesResult(locations), cancellationToken)
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

/// <summary>
/// Builds the client-originated "GoToHook command executed" event (issue #861). Kept on a plain
/// static class for the same unit-testability reason as <see cref="HookLocationsMapper"/>.
/// </summary>
/// <remarks>
/// The command's only placement is the editor context menu, so every invocation reports
/// <see cref="GoToHookSources.ContextMenu"/>. The classic CodeLens click path does not run through
/// the command and (issue #698) cannot be told apart from the Details-popup prefetch, so it emits
/// nothing; a keyboard binding the user assigns to the command is indistinguishable from the menu.
/// </remarks>
internal static class GoToHookTelemetry
{
    public static GenericEvent CreateEvent(string source) =>
        new(TelemetryEvents.GoToHookCommandExecuted,
            [new KeyValuePair<string, object>(GoToHookSources.PropertyName, source)]);
}

/// <summary>
/// Maps applicable hooks onto <see cref="StepUsageLocation"/>, the Find All References window's
/// row type from Find Step Definition Usages — the same pipeline <c>HookMatchCountCodeLens</c>
/// already reuses for matching scenarios (issue #315). <c>StepText</c> is supplied explicitly so
/// the Code column shows the hook's type and method name instead of falling back to reading the
/// source line from disk.
/// </summary>
/// <remarks>
/// Kept on a plain static class (not on <see cref="GoToHooksCommand"/> itself) so it can be
/// unit-tested without pulling in a reference to the VS/COM <c>Command</c> base type — same
/// rationale as <c>RenameStepLabelParser</c>.
/// </remarks>
internal static class HookLocationsMapper
{
    public static IReadOnlyList<StepUsageLocation> BuildLocations(IReadOnlyList<HookLocation> hooks)
    {
        var locations = new List<StepUsageLocation>(hooks.Count);
        foreach (var h in hooks)
        {
            var stepText = $"[{h.HookType}] {h.MethodName}";
            locations.Add(new StepUsageLocation(
                fileUri:   h.Uri,
                startLine: h.StartLine,
                startChar: h.StartChar,
                endLine:   h.StartLine,
                endChar:   h.StartChar,
                stepText:  stepText));
        }
        return locations;
    }
}
