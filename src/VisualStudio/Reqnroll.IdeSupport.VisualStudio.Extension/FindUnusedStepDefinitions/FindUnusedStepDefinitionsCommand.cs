#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// "Find Unused Step Definitions" command placed in the Reqnroll submenu under Extensions.
/// Unlike Find Step Definition Usages, this command is not scoped to a C# editor — it is a workspace-wide operation
/// available whenever the server is running.
/// </summary>
[VisualStudioContribution]
internal sealed class FindUnusedStepDefinitionsCommand : Command
{
    private readonly FindUnusedStepDefinitionsState _state;
    private readonly ILogger<FindUnusedStepDefinitionsCommand> _logger;

    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public FindUnusedStepDefinitionsCommand(
        FindUnusedStepDefinitionsState state,
        ILogger<FindUnusedStepDefinitionsCommand> logger)
    {
        _state  = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Find Unused Step Definitions")
    {
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),
        // No VisibleWhen constraint — available in any context once the server is running.
        Placements = [],  // Placed via ReqnrollMenu only; no context-menu placement for this command.
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("FindUnusedStepDefinitionsCommand: invoked.");

            var service  = _state.Service;
            var renderer = _state.Renderer;
            if (service is null || renderer is null)
            {
                _logger.LogWarning(
                    "FindUnusedStepDefinitionsCommand: LSP server not yet initialized (service={ServiceState}, renderer={RendererState}).",
                    service is null ? "null" : "set", renderer is null ? "null" : "set");
                VsUtils.ShowStatusBarMessage("Reqnroll: LSP server not yet initialized — open a .feature file to activate it.");
                return;
            }

            var result = await service.FindUnusedAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "FindUnusedStepDefinitionsCommand: {ItemCount} unused step definition(s).", result.Items.Count);

            await renderer.RenderAsync(result, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("FindUnusedStepDefinitionsCommand: render complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUnusedStepDefinitionsCommand: failed.");
        }
    }
}
