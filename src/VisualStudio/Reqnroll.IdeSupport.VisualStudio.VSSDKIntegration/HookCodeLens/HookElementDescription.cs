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
            entry.OwnLevelOnly ? "1" : "0");

    public static bool TryDecode(string? elementDescription, out int line, out int navLine, out int navChar, out bool ownLevelOnly)
    {
        line = navLine = navChar = 0;
        ownLevelOnly = false;

        var parts = elementDescription?.Split('|');
        if (parts is not { Length: 4 })
            return false;

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out line)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out navLine)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out navChar)
            && TryDecodeBool(parts[3], out ownLevelOnly);
    }

    private static bool TryDecodeBool(string value, out bool result)
    {
        result = value == "1";
        return value is "1" or "0";
    }
}
