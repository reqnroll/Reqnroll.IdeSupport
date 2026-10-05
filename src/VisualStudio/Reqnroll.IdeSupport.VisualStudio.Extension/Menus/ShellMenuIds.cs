#nullable enable

using System;
using Reqnroll.IdeSupport.VisualStudio.WellKnownIds;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Menus;

/// <summary>
/// VS shell menu identifiers used by the VisualStudio.Extensibility commands'
/// <c>CommandPlacement.VsctParent</c> placements.
/// </summary>
/// <remarks>
/// VisualStudio.Extensibility evaluates each <c>CommandConfiguration</c> at build time and cannot load
/// <c>Microsoft.VisualStudio.Shell.15.0</c> ("has references outside of netstandard2.0"), so the SDK
/// constant <c>VsMenus.guidSHLMainMenu</c> cannot be referenced from a command configuration directly.
/// This copy is asserted equal to it, and to <c>vsshlids.h</c>, by <c>VsSdkHeaderConstantsTests</c>.
/// </remarks>
internal static class ShellMenuIds
{
    /// <summary><c>guidSHLMainMenu</c> (<c>vsshlids.h</c>): the VS shell's built-in command set.</summary>
    internal static readonly Guid GuidSHLMainMenu = new("{D309F791-903F-11D0-9EFC-00A0C911004F}");

    /// <summary>See <see cref="VsWellKnownIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION"/>.</summary>
    internal const int IDG_VS_CODEWIN_NAVIGATETOLOCATION = VsWellKnownIds.IDG_VS_CODEWIN_NAVIGATETOLOCATION;
}
