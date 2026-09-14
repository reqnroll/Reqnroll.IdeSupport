#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.FindAllReferences;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Opens the VS Find All References tool window and populates it with
/// <see cref="UnusedStepDefinitionsResult"/> locations (Find Unused Step Definitions).
/// </summary>
internal sealed class FindUnusedStepDefinitionsRenderer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FindUnusedStepDefinitionsRenderer> _logger;

    /// <summary>Creates the renderer over the extension's service provider.</summary>
    public FindUnusedStepDefinitionsRenderer(IServiceProvider serviceProvider, ILogger<FindUnusedStepDefinitionsRenderer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    /// <summary>Opens the Find All References window titled with the unused-count summary and populates it with <paramref name="result"/>.</summary>
    public async Task RenderAsync(
        UnusedStepDefinitionsResult result,
        CancellationToken           cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var far = _serviceProvider.GetService(typeof(SVsFindAllReferences)) as IFindAllReferencesService;
        if (far is null)
        {
            _logger.LogWarning("FindUnusedStepDefinitionsRenderer: IFindAllReferencesService not available.");
            return;
        }

        var count = result.Items.Count;
        var label = count == 0
            ? "Reqnroll: 0 unused step definitions"
            : $"Reqnroll: {count} unused step definition{(count == 1 ? "" : "s")}";

        var window = far.StartSearch(label);
        if (window is null)
        {
            _logger.LogWarning("FindUnusedStepDefinitionsRenderer: StartSearch returned null window.");
            return;
        }

        var dataSource = new UnusedStepDefinitionsDataSource(result.Items);
        window.Manager.AddSource(dataSource,
            StandardTableKeyNames.Text,           // "ClassName.MethodName  ·  Expression" in Code column
            StandardTableKeyNames.DocumentName,
            StandardTableKeyNames.Line,
            StandardTableKeyNames.Column,
            StandardTableKeyNames.ProjectName);
        // Note: StandardTableKeyNames.Definition is NOT declared here.  The VS FAR window
        // type-checks its value for a Roslyn DefinitionBucket; plain strings are ignored and
        // produce "[Definition:Unknown]".  "description" is also omitted — declaring it causes
        // VS to auto-generate a Description column that duplicates the Code column text.

        _logger.LogDebug(
            "FindUnusedStepDefinitionsRenderer: opened FAR window {Label} with {ItemCount} item(s)",
            label, count);
    }
}
