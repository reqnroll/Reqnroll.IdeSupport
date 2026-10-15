using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Menus;

/// <summary>
/// The Reqnroll command group in the code-editor context menu. VS draws a separator before and after
/// a command group, so the Reqnroll items stay visually distinct from the built-in entries without a
/// submenu. Each command's own <c>VisibleWhen</c> decides what shows in .feature vs C# editors.
/// </summary>
[VisualStudioContribution]
internal static class ReqnrollContextMenu
{
    /// <summary>Group placed directly in the code-editor context menu (<c>IDM_VS_CTXT_CODEWIN</c>).</summary>
    [VisualStudioContribution]
    public static CommandGroupConfiguration ReqnrollEditorGroup => new(
        GroupPlacement.VsctParent(ShellMenuIds.GuidSHLMainMenu, id: ShellMenuIds.IDM_VS_CTXT_CODEWIN, priority: 0x0100))
    {
        Children =
        [
            GroupChild.Command<GoToHooksCommand>(),
            GroupChild.Command<FindUnusedStepDefinitionsCommand>(),
            GroupChild.Command<RenameStepCommand>(),
        ],
    };
}
