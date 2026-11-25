#nullable enable

using System.Globalization;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Turns the server's hook lenses into entries for the shared
/// <see cref="FeatureCodeLensTaggerProvider"/>.
/// </summary>
internal static class HookLensTagEntries
{
    /// <summary>
    /// The tag entry for one hook lens. The server marks the step-hooks lens with
    /// <see cref="HookFeatureLensEntry.AlwaysShowPicker"/>, the same flag
    /// <see cref="HookCodeLensDataPoint"/> uses to pick out its own entry. The revision key covers
    /// everything the data point renders or navigates to, so any change to it yields a new tag.
    /// </summary>
    internal static FeatureLineLensEntry ToTagEntry(HookFeatureLensEntry entry) =>
        new(entry.Line,
            entry.AlwaysShowPicker ? LineLensKinds.StepHooks : LineLensKinds.Hooks,
            string.Join(",",
                entry.Title,
                entry.NavLine.ToString(CultureInfo.InvariantCulture),
                entry.NavChar.ToString(CultureInfo.InvariantCulture),
                entry.OwnLevelOnly ? "1" : "0",
                entry.AlwaysShowPicker ? "1" : "0"));
}
