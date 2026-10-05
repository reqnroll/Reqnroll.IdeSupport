using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Menus;

/// <summary>
/// Adds a "Reqnroll" submenu to the Extensions top-level menu.
/// Note: VS.Extensibility's <see cref="MenuConfiguration"/> does not support icons on menus —
/// the icon is carried by the child commands instead.
/// </summary>
[VisualStudioContribution]
internal static class ReqnrollMenu
{
    /// <summary>Menu configuration for the "Reqnroll" submenu under the Extensions menu.</summary>
    [VisualStudioContribution]
    public static MenuConfiguration ReqnrollExtensionsMenu => new("Reqnroll")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Children =
        [
            MenuChild.Command<FindStepUsagesCommand>(),
            MenuChild.Command<FindUnusedStepDefinitionsCommand>(),
            MenuChild.Command<RenameStep.RenameStepCommand>(),
        ],
    };
}
