#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Opens the VS Find All References tool window and populates it with
/// <see cref="StepUsagesResult"/> locations (Find Step Definition Usages, design doc section P3b).
/// Must be constructed on any thread but <see cref="RenderAsync"/> switches to the
/// UI thread internally before calling VS services.
/// </summary>
internal sealed class FindStepUsagesRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FindStepUsagesRenderer> _logger;

    /// <summary>Creates the renderer with the VS service provider for opening the Find All References window.</summary>
    public FindStepUsagesRenderer(IServiceProvider serviceProvider, ILogger<FindStepUsagesRenderer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    /// <summary>
    /// Opens the Find All References window with <paramref name="label"/> as the title,
    /// then pushes all locations from <paramref name="result"/> into it.
    /// Silently does nothing if the VS service is unavailable.
    /// </summary>
    public async Task RenderAsync(
        string            label,
        StepUsagesResult  result,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var far = _serviceProvider.GetService(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
        if (far is null)
        {
            _logger.LogWarning("FindStepUsagesRenderer: IFindAllReferencesService not available.");
            return;
        }

        var window = far.StartSearch(label);
        if (window is null)
        {
            _logger.LogWarning("FindStepUsagesRenderer: StartSearch returned null window.");
            return;
        }

        var dataSource = new FeatureReferencesDataSource(result.Locations);
        window.Manager.AddSource(dataSource,
            StandardTableKeyNames.DocumentName,
            StandardTableKeyNames.Line,
            StandardTableKeyNames.Column,
            StandardTableKeyNames.Text,
            StandardTableKeyNames.ProjectName,
            "description");

        _logger.LogDebug(
            "FindStepUsagesRenderer: opened FAR window {Label} with {LocationCount} location(s)",
            label, result.Locations.Count);
    }
}
