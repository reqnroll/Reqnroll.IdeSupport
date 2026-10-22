#nullable enable

using System.Collections.Generic;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using System.IO;
using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Runs Go To Definition for a <c>.feature</c> caret position on behalf of
/// <c>GoToDefinitionCommandFilter</c>: no definition → status-bar message, one →
/// navigate straight to it, several → the Find All References window titled after the step
/// (<see cref="StepDefinitionsWindowTitle"/>) instead of VS's <c>'{word}' declarations</c>, listing
/// each binding as <c>Class.Method · expression</c>.
/// </summary>
internal sealed class GoToStepDefinitionPresenter
{
    private readonly FindStepDefinitionsService _service;
    private readonly StepDefinitionsRenderer _renderer;
    private readonly IIdeSupportLogger _navigationLogger;
    private readonly ILogger<GoToStepDefinitionPresenter> _logger;
    private readonly Action<int>? _goToDefinitionRun;

    /// <summary>
    /// Creates the presenter over the definition service and the shared step-definitions renderer.
    /// <paramref name="goToDefinitionRun"/> is called with the navigable row count each time the user runs
    /// Go To Definition; the extension turns it into the "Command.GoToStepDefinition" event.
    /// </summary>
    public GoToStepDefinitionPresenter(
        FindStepDefinitionsService service,
        StepDefinitionsRenderer renderer,
        IIdeSupportLogger navigationLogger,
        ILogger<GoToStepDefinitionPresenter> logger,
        Action<int>? goToDefinitionRun = null)
    {
        _goToDefinitionRun = goToDefinitionRun;
        _service          = service;
        _renderer         = renderer;
        _navigationLogger = navigationLogger;
        _logger           = logger;
    }

    /// <summary>Matches <c>GoToDefinitionRedirect.HasNavigableDefinitionAsync</c>.</summary>
    public Task<bool> HasNavigableDefinitionAsync(
        string fileUri, int line0, int char0, CancellationToken cancellationToken) =>
        _service.HasNavigableDefinitionAsync(fileUri, line0, char0, cancellationToken);

    /// <summary>Matches <c>GoToDefinitionRedirect.GoToDefinitionAsync</c>.
    public async Task GoToDefinitionAsync(
        string            fileUri,
        int               line0,
        int               char0,
        string            caretLineText,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StepDefinitionListItem> items;
        try
        {
            // A cancelled request throws OperationCanceledException rather than reading as "no step
            // definition found" for a press that a newer one superseded; the caller
            // (GoToDefinitionCommandFilter) treats that as a quiet cancellation.
            items = await _service
                .GetDefinitionsAsync(fileUri, line0, char0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (LspRequestFailure.TryGetReason(ex, out var reason))
        {
            // A failed lookup is not "no step definition found".
            _logger.LogWarning(
                "GoToStepDefinitionPresenter: lookup failed at {FileUri}:{Line0}:{Char0}: {Reason}", fileUri, line0, char0, reason);
            await ShowStatusAsync($"Reqnroll: Go to Step Definition failed - {reason}.", cancellationToken);
            return;
        }

        _goToDefinitionRun?.Invoke(items.Count(item => item.IsResolved && item.SourceFile is { Length: > 0 }));

        if (items.Count == 0)
        {
            _logger.LogInformation(
                "GoToStepDefinitionPresenter: no step definition at {FileUri}:{Line0}:{Char0}.", fileUri, line0, char0);
            await ShowStatusAsync("Reqnroll: No step definition found for the step at the caret.", cancellationToken);
            return;
        }

        if (items.Count == 1)
        {
            var item = items[0];
            if (!item.IsResolved || item.SourceFile is not { Length: > 0 })
            {
                // Same wording as the unresolved row in the Find Unused Step Definitions list.
                _logger.LogInformation(
                    "GoToStepDefinitionPresenter: {ClassName}.{MethodName} has no local source (recorded at {RecordedSourceFile}).",
                    item.ClassName, item.MethodName, item.RecordedSourceFile);
                await ShowStatusAsync(
                    $"Reqnroll: {item.ClassName}.{item.MethodName} — source not on this machine (rebuild locally).",
                    cancellationToken);
                return;
            }

            var target = new NavigationTarget(
                $"{Path.GetFileName(item.SourceFile)}:{item.SourceLine + 1}", item.SourceFile, item.SourceLine, item.SourceChar);
            await NavigationPickerHelper
                .PickAndNavigateAsync(new[] { target }, _navigationLogger, "Go to Step Definition", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var title = StepDefinitionsWindowTitle.Build(caretLineText, items.Count);
        _logger.LogInformation(
            "GoToStepDefinitionPresenter: {ItemCount} step definitions; rendering {Title}.", items.Count, title);
        await _renderer.RenderAsync(title, items, cancellationToken).ConfigureAwait(false);
    }

    private static Task ShowStatusAsync(string message, CancellationToken cancellationToken) =>
        VsUtils.ShowStatusBarMessageAsync(message, cancellationToken);
}
