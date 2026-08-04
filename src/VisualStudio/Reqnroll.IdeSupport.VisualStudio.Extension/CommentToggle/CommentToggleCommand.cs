#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.CommentToggle;

/// <summary>
/// "Comment/Uncomment" command — placed in the editor context menu,
/// visible only when a <c>.feature</c> file editor is active (design doc: Comment/Uncomment toggle).
/// </summary>
/// <remarks>
/// When invoked on selected lines, sends <c>workspace/executeCommand</c> for
/// <c>reqnroll.toggleComment</c> to the LSP server. The server responds with a
/// <c>workspace/applyEdit</c> notification that VS applies natively.
/// </remarks>
[VisualStudioContribution]
internal sealed class CommentToggleCommand : Command
{
    // guidSHLMainMenu — VS shell main-menu set that owns all code-editor context-menu
    // groups.  Every working placement in this extension uses this GUID.
    private static readonly Guid GuidSHLMainMenu = new("{d309f791-903f-11d0-9efc-00a0c911004f}");

    // IDG_VS_CODEWIN_FINDREF (vsshlids.h 0x02B1) — Navigate group in the code-editor
    // context menu.  VS surfaces this group for LSP-owned documents; the Edit group
    // (0x02AD) is not shown for LSP files.  All other working Reqnroll commands
    // (GoToHooks, GoToDefinition, FindStepUsages) also use this group.
    private const int IDG_VS_CODEWIN_FINDREF = 0x02B1;

    private readonly CommentToggleState _state;
    private readonly ILogger<CommentToggleCommand> _logger;

    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public CommentToggleCommand(CommentToggleState state, ILogger<CommentToggleCommand> logger)
    {
        _state  = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Comment/Uncomment")
    {
        Icon        = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),

        // Show only when a .feature file editor is active; invisible in all other editors.
        VisibleWhen = ActivationConstraint.EditorContentType("gherkin"),

        // Placed in the edit group of the code-editor context menu
        Placements  =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, IDG_VS_CODEWIN_FINDREF, 0x0300),
        ],
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("CommentToggleCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _logger.LogWarning("CommentToggleCommand: LSP server not yet initialized.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _logger.LogWarning("CommentToggleCommand: no active text view.");
                return;
            }

            var fileUri = textView.Uri.ToString();

            // Determine the line range from the selection.
            // If nothing is selected (collapsed selection), use the current line only.
            var selection = textView.Selection;
            var startPos  = selection.Start;
            var endPos    = selection.End;

            int startLine, endLine;
            if (startPos == endPos)
            {
                // No selection — use the current line.
                var line = startPos.GetContainingLine();
                startLine = line.LineNumber;
                endLine   = line.LineNumber;
                _logger.LogInformation(
                    "CommentToggleCommand: no selection — using current line {StartLine}.", startLine);
            }
            else
            {
                startLine = startPos.GetContainingLine().LineNumber;
                var endContainingLine = endPos.GetContainingLine();
                endLine = CommentToggleLineRange.AdjustEndLineForWholeLineSelection(
                    startLine, endContainingLine.LineNumber,
                    endPos.Offset == endContainingLine.Text.Start.Offset);
                _logger.LogInformation(
                    "CommentToggleCommand: selection lines [{StartLine}..{EndLine}].", startLine, endLine);
            }

            _logger.LogInformation(
                "CommentToggleCommand: uri={FileUri}, lines [{StartLine}..{EndLine}]",
                fileUri, startLine, endLine);

            await service
                .ToggleCommentAsync(fileUri, startLine, endLine, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("CommentToggleCommand: completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CommentToggleCommand: failed.");
        }
    }
}
