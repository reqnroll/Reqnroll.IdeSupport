#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// The command group/id the hook-match-count lens's Details popup entries invoke to navigate to a
/// hook (issue #372) — routed through <c>ReqnrollPluginPackage</c>'s <c>IOleCommandTarget</c>,
/// mirroring Microsoft's <c>CodeLensOopSample</c>.
/// </summary>
public static class HookCodeLensCommandIds
{
    /// <summary>Command set GUID for hook-match-count lens navigation commands.</summary>
    public static readonly Guid CommandSet = new("F7A6B3C2-8E5D-4A1B-9C3E-2D6F1A8B4C7E");

    /// <summary>Navigates to the hook definition named by the entry's <c>NavigationCommandArgs</c> (a single <c>"uri|line|char"</c>-encoded string).</summary>
    public const int NavigateToHookCommandId = 0x1000;
}
