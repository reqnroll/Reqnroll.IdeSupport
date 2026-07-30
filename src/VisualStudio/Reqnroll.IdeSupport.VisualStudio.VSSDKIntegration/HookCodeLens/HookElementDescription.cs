#nullable enable

using System.Globalization;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Encodes/decodes the fields <see cref="HookCodeLensTagger"/> needs to smuggle through
/// <c>ICodeLensDescriptor.ElementDescription</c> to <see cref="HookCodeLensDataPointProvider"/>
/// (issue #372) — the classic CodeLens remoting contract carries no buffer/span access into the
/// data-point-provider side, so this pipe-delimited string is the one channel the tagger controls
/// end to end.
/// </summary>
internal static class HookElementDescription
{
    public static string Encode(HookFeatureLensEntry entry) =>
        string.Join("|",
            entry.Line.ToString(CultureInfo.InvariantCulture),
            entry.NavLine.ToString(CultureInfo.InvariantCulture),
            entry.NavChar.ToString(CultureInfo.InvariantCulture),
            entry.OwnLevelOnly ? "1" : "0",
            entry.AlwaysShowPicker ? "1" : "0");

    /// <summary>
    /// Decodes an <c>ElementDescription</c>. <paramref name="isStepHooksLens"/> (the server's
    /// <c>alwaysShowPicker</c> argument, see <see cref="HookFeatureLensEntry.AlwaysShowPicker"/>) is
    /// the only field that actually distinguishes the two lens kinds that can share the same
    /// Scenario: line — <paramref name="ownLevelOnly"/> is <see langword="true"/> for both, since
    /// <c>HookCodeLensHandler</c> passes <c>ownLevelOnly: true</c> to both
    /// <c>AddOwnLevelLens</c> and <c>AddStepHooksLens</c>. <see cref="HookCodeLensDataPointProvider"/>
    /// and <see cref="StepHooksCodeLensDataPointProvider"/> each claim only their own kind via this
    /// flag, so classic CodeLens's one-indicator-per-(provider, span) behavior gives each kind its
    /// own indicator on the shared line instead of one silently overwriting the other.
    /// </summary>
    public static bool TryDecode(string? elementDescription, out int line, out int navLine, out int navChar, out bool ownLevelOnly, out bool isStepHooksLens)
    {
        line = navLine = navChar = 0;
        ownLevelOnly = false;
        isStepHooksLens = false;

        var parts = elementDescription?.Split('|');
        if (parts is not { Length: 5 })
            return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out line)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out navLine)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out navChar)
            && TryDecodeBool(parts[3], out ownLevelOnly)
            && TryDecodeBool(parts[4], out isStepHooksLens);
    }

    private static bool TryDecodeBool(string value, out bool result)
    {
        result = value == "1";
        return value is "1" or "0";
    }
}
