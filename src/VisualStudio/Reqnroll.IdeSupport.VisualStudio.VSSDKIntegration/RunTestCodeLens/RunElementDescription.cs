#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Encodes/decodes what <see cref="RunTestCodeLensTagger"/> smuggles through
/// <c>ICodeLensDescriptor.ElementDescription</c> to the OOP data point provider — the classic
/// CodeLens remoting contract carries no buffer/span access into the data-point-provider side.
/// Mirrors <c>HookElementDescription</c> exactly.
/// </summary>
internal static class RunElementDescription
{
    /// <summary>Encodes the descriptor for <paramref name="line"/>, covering every target the server resolved for it.</summary>
    public static string Encode(int line, IEnumerable<RunTestTargetEntry> entriesOnLine)
    {
        var revision = string.Join(";", entriesOnLine
            .OrderBy(e => e.DeclaringTypeFullName).ThenBy(e => e.MethodName)
            .Select(e => e.DeclaringTypeFullName + "," + e.MethodName));

        return string.Concat(line.ToString(CultureInfo.InvariantCulture), "|", revision);
    }

    /// <summary>Decodes the 0-based line a descriptor refers to. The revision component is deliberately not surfaced — a data point resolves its own targets from a live callback fetch, not from the descriptor.</summary>
    public static bool TryDecode(string? elementDescription, out int line)
    {
        line = 0;

        var separator = elementDescription?.IndexOf('|') ?? -1;
        if (elementDescription is null || separator < 0)
            return false;

        return int.TryParse(
            elementDescription.Substring(0, separator),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out line);
    }
}
