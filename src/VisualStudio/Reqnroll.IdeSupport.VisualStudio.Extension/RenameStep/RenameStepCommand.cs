#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// "Rename Step" command — available from both the C# and <c>.feature</c> editor context menus.
/// Queries the server for renameable binding targets at the caret, prompts for the new step text,
/// and sends <c>textDocument/rename</c>; the server applies the resulting edit via
/// <c>workspace/applyEdit</c>.
/// </summary>
[VisualStudioContribution]
internal sealed class RenameStepCommand : Command
{
    private readonly RenameStepState _state;
    private readonly ILogger<RenameStepCommand> _logger;

    private static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");
    private const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = 0x02B1;

    /// <summary>Creates the command over the shared runtime state holder.</summary>
    public RenameStepCommand(RenameStepState state, ILogger<RenameStepCommand> logger)
    {
        _state  = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("Rename Step")
    {
        Icon = new CommandIconConfiguration(ImageMoniker.Custom("ReqnrollIcon"), IconSettings.IconAndText),
        VisibleWhen = ActivationConstraint.Or(
            ActivationConstraint.EditorContentType("CSharp"),
            ActivationConstraint.EditorContentType("gherkin")),
        Placements =
        [
            CommandPlacement.VsctParent(GuidSHLMainMenu, id: IDG_VS_CODEWIN_NAVIGATETOLOCATION, priority: 0x0100),
        ],
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("RenameStepCommand: invoked.");

            var service = _state.Service;
            if (service is null)
            {
                _logger.LogWarning("RenameStepCommand: LSP server not yet initialized.");
                VsUtils.ShowStatusBarMessage("Reqnroll: LSP server not yet initialized.");
                return;
            }

            var textView = await context.GetActiveTextViewAsync(cancellationToken).ConfigureAwait(false);
            if (textView is null)
            {
                _logger.LogWarning("RenameStepCommand: no active text view in client context.");
                return;
            }

            var fileUri  = textView.Uri.ToString();
            var caretPos = textView.Selection.ActivePosition;
            var line     = caretPos.GetContainingLine();
            var lineNum  = line.LineNumber;
            var charNum  = caretPos.Offset - line.Text.Start;

            _logger.LogInformation(
                "RenameStepCommand: active view uri={FileUri}, caret line={LineNum} char={CharNum}.", fileUri, lineNum, charNum);

            // Step 1: Get rename targets from the server
            var targets = await service.GetRenameTargetsAsync(fileUri, lineNum, charNum, cancellationToken)
                .ConfigureAwait(false);

            if (targets is null || targets.Targets.Count == 0)
            {
                _logger.LogInformation("RenameStepCommand: no renameable targets at cursor position.");
                VsUtils.ShowStatusBarMessage("Reqnroll: No step definition found to rename at this position.");
                return;
            }

            // Step 2: Select target (picker if multiple)
            int selectedAttributeIndex;
            string currentLabel;
            string currentExpression;

            if (targets.Targets.Count == 1)
            {
                var item = targets.Targets[0];
                selectedAttributeIndex = item.AttributeIndex;
                currentLabel           = item.Label;
                currentExpression      = item.Expression;
                _logger.LogInformation(
                    "RenameStepCommand: single target, attributeIndex={AttributeIndex}, label={Label}.",
                    selectedAttributeIndex, currentLabel);
            }
            else
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var pickerTargets = targets.Targets
                    .Select(t => new NavigationTarget(t.Label, textView.Uri.LocalPath, 0, 0))
                    .ToList();

                var dialog = new NavigationPickerDialog("Choose step definition to rename", pickerTargets);
                if (dialog.ShowModal() != true || dialog.SelectedIndex < 0)
                {
                    _logger.LogInformation("RenameStepCommand: picker dismissed.");
                    return;
                }

                var chosen = targets.Targets[dialog.SelectedIndex];
                selectedAttributeIndex = chosen.AttributeIndex;
                currentLabel           = chosen.Label;
                currentExpression      = chosen.Expression;
                _logger.LogInformation(
                    "RenameStepCommand: user selected target index={SelectedIndex}, attributeIndex={AttributeIndex}.",
                    dialog.SelectedIndex, selectedAttributeIndex);
            }

            // Step 3: Tell the server which attribute was selected
            await service.SelectRenameTargetAsync(fileUri, version: 0, selectedAttributeIndex, cancellationToken)
                .ConfigureAwait(false);

            // Step 4: Prompt user for new step text
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var currentStepText = !string.IsNullOrEmpty(currentExpression)
                ? currentExpression
                : RenameStepLabelParser.ExtractStepTextFromLabel(currentLabel);

            var newStepText = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the new step text:", "Rename Step", currentStepText);
            if (string.IsNullOrEmpty(newStepText))
            {
                _logger.LogInformation("RenameStepCommand: user cancelled rename dialog.");
                return;
            }

            _logger.LogInformation("RenameStepCommand: user entered new text {NewStepText}.", newStepText);

            // Step 5: Send textDocument/rename via the service
            var result = await service.SendRenameRequestAsync(
                fileUri, lineNum, charNum, newStepText, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogInformation("RenameStepCommand: server returned null from rename.");
                VsUtils.ShowStatusBarMessage("Reqnroll: Rename failed.");
                return;
            }

            // The server already applied the edit natively via workspace/applyEdit before this
            // request's response reached us — nothing left to apply here.
            _logger.LogInformation("RenameStepCommand: rename result = {Result}", result);
            _logger.LogInformation("RenameStepCommand: rename completed successfully.");
            VsUtils.ShowStatusBarMessage("Reqnroll: Step renamed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameStepCommand: failed.");
        }
    }
}

/// <summary>
/// Splits a picker label of the form <c>"Given step text"</c> at the first space,
/// returning everything after the keyword prefix. Kept on a plain static class (not on
/// <see cref="RenameStepCommand"/> itself) so it can be unit-tested without pulling in a
/// reference to the VS/COM <c>Command</c> base type.
/// </summary>
internal static class RenameStepLabelParser
{
    public static string ExtractStepTextFromLabel(string label)
    {
        var space = label.IndexOf(' ');
        var prefix = space >= 0 ? label.Substring(0, space + 1) : "";
        return label.Length > prefix.Length ? label.Substring(prefix.Length) : label;
    }
}
