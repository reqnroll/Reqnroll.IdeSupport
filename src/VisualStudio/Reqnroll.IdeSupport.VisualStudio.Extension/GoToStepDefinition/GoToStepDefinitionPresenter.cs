#nullable enable

using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Runs Go To Definition for a <c>.feature</c> caret position on behalf of
/// <c>GoToDefinitionCommandFilter</c> (issue #757): no definition → status-bar message, one →
/// navigate straight to it, several → the Find All References window titled after the step
/// (<see cref="StepDefinitionsWindowTitle"/>) instead of VS's <c>'{word}' declarations</c>.
/// </summary>
internal sealed class GoToStepDefinitionPresenter
{
    private readonly GoToStepDefinitionService _service;
    private readonly FindStepUsagesRenderer _renderer;
    private readonly IIdeSupportLogger _navigationLogger;
    private readonly ILogger<GoToStepDefinitionPresenter> _logger;

    /// <summary>Creates the presenter over the definition service and the shared Find All References renderer.</summary>
    public GoToStepDefinitionPresenter(
        GoToStepDefinitionService service,
        FindStepUsagesRenderer renderer,
        IIdeSupportLogger navigationLogger,
        ILogger<GoToStepDefinitionPresenter> logger)
    {
        _service          = service;
        _renderer         = renderer;
        _navigationLogger = navigationLogger;
        _logger           = logger;
    }

    /// <summary>Matches <c>GoToDefinitionRedirect.GoToDefinitionAsync</c>.</summary>
    public async Task GoToDefinitionAsync(
        string            fileUri,
        int               line0,
        int               char0,
        string            caretLineText,
        CancellationToken cancellationToken)
    {
        var locations = await _service
            .GetDefinitionsAsync(fileUri, line0, char0, cancellationToken)
            .ConfigureAwait(false);

        if (locations.Count == 0)
        {
            _logger.LogInformation(
                "GoToStepDefinitionPresenter: no step definition at {FileUri}:{Line0}:{Char0}.", fileUri, line0, char0);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            VsUtils.ShowStatusBarMessage("Reqnroll: No step definition found for the step at the caret.");
            return;
        }

        if (locations.Count == 1)
        {
            var target = ToNavigationTarget(locations[0]);
            if (target is not null)
            {
                await NavigationPickerHelper
                    .PickAndNavigateAsync(new[] { target }, _navigationLogger, "Go to Step Definition", cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        var title = StepDefinitionsWindowTitle.Build(caretLineText, locations.Count);
        _logger.LogInformation(
            "GoToStepDefinitionPresenter: {LocationCount} step definitions; rendering {Title}.", locations.Count, title);

        // FeatureReferencesDataSource falls back to reading the target line from disk when no step
        // text is supplied, which for a step definition is its method declaration — the same Code
        // column VS's own definition list showed.
        var rows = locations
            .Select(l => new StepUsageLocation(l.FileUri, l.StartLine, l.StartChar, l.StartLine, l.StartChar))
            .ToList();
        await _renderer.RenderAsync(title, new StepUsagesResult(rows), cancellationToken).ConfigureAwait(false);
    }

    private static NavigationTarget? ToNavigationTarget(StepDefinitionLocation location)
    {
        if (!Uri.TryCreate(location.FileUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            return null;

        var filePath = uri.LocalPath;
        return new NavigationTarget(
            $"{Path.GetFileName(filePath)}:{location.StartLine + 1}", filePath, location.StartLine, location.StartChar);
    }
}
