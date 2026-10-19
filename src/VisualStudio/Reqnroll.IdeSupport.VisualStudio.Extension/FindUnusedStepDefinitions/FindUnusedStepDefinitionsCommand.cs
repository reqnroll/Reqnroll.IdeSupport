#nullable enable

using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension.Documents;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// "Find Unused Step Definitions" command placed in the Reqnroll group of the editor context menu.
/// It is a workspace-wide operation, not tied to the caret, and is offered from the C# editor
/// context menu only (the Reqnroll group in a .feature editor offers Find Hooks and Rename Step...
/// instead); available whenever the server is running.
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
    public override CommandConfiguration CommandConfiguration => new("Find Unused Step Definitions in Projects")
    {
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),
        // Shown in C# editors only; placed via ReqnrollContextMenu.cs.
        VisibleWhen = ActivationConstraint.EditorContentType(CSharpDocumentType.CSharp),
        Placements = [],
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
                await VsUtils.ShowStatusBarMessageAsync("Reqnroll: LSP server not yet initialized — open a .feature file to activate it.");
                return;
            }

            var result = await service.FindUnusedAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "FindUnusedStepDefinitionsCommand: {ItemCount} unused step definition(s).", result.Items.Count);

            await renderer.RenderAsync(result, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("FindUnusedStepDefinitionsCommand: render complete.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FindUnusedStepDefinitionsCommand: cancelled.");
        }
        catch (Exception ex) when (LspRequestFailure.TryGetReason(ex, out var reason))
        {
            // Say the request failed, rather than present it as an empty answer.
            _logger.LogWarning("FindUnusedStepDefinitionsCommand: request failed: {Reason}", reason);
            await VsUtils.ShowStatusBarMessageAsync($"Reqnroll: Find Unused Step Definitions failed - {reason}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUnusedStepDefinitionsCommand: failed.");
        }
    }
}
