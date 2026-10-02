#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Opens the VS Find All References tool window and populates it with step-definition rows
/// (<see cref="StepDefinitionsDataSource"/>): the unused step definitions (Find Unused Step
/// Definitions) and the step definitions matching a step (Go To Definition with several
/// matches, issue #757).
/// </summary>
internal sealed class StepDefinitionsRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StepDefinitionsRenderer> _logger;

    /// <summary>Creates the renderer over the extension's service provider.</summary>
    public StepDefinitionsRenderer(IServiceProvider serviceProvider, ILogger<StepDefinitionsRenderer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    /// <summary>Opens the Find All References window titled with the unused-count summary and populates it with <paramref name="result"/>.</summary>
    public Task RenderAsync(
        UnusedStepDefinitionsResult result,
        CancellationToken           cancellationToken)
    {
        var count = result.Items.Count;
        var label = count == 0
            ? "Reqnroll: 0 unused step definitions"
            : $"Reqnroll: {count} unused step definition{(count == 1 ? "" : "s")}";

        return RenderAsync(label, result.Items, cancellationToken);
    }

    /// <summary>Opens the Find All References window titled <paramref name="label"/> and populates it with <paramref name="items"/>.</summary>
    public async Task RenderAsync(
        string                                label,
        IReadOnlyList<StepDefinitionListItem> items,
        CancellationToken                     cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var far = _serviceProvider.GetService(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
        if (far is null)
        {
            _logger.LogWarning("StepDefinitionsRenderer: IFindAllReferencesService not available.");
            return;
        }

        var window = far.StartSearch(label);
        if (window is null)
        {
            _logger.LogWarning("StepDefinitionsRenderer: StartSearch returned null window.");
            return;
        }

        // The Code column is the window's own fixed "linetext" column, which reads each entry's
        // Text value without it being declared here. Declaring StandardTableKeyNames.Text would add
        // the separate "text" column — VS's wrapping, Error-List-style "Description" column — showing
        // the same value again (confirmed by decompiling TextColumnDefinition / LineTextColumnDefinition,
        // issue #757). StandardTableKeyNames.Definition is not declared either: grouping needs a
        // DefinitionBucket value, which these entries do not supply.
        window.Manager.AddSource(new StepDefinitionsDataSource(items),
            StandardTableKeyNames.DocumentName,
            StandardTableKeyNames.Line,
            StandardTableKeyNames.Column,
            StandardTableKeyNames.ProjectName);

        _logger.LogDebug(
            "StepDefinitionsRenderer: opened FAR window {Label} with {ItemCount} item(s)",
            label, items.Count);
    }
}
