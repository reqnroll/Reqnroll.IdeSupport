#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Encodes/decodes what <see cref="HookCodeLensTagger"/> smuggles through
/// <c>ICodeLensDescriptor.ElementDescription</c> to the data point providers (issue #372) — the
/// classic CodeLens remoting contract carries no buffer/span access into the data-point-provider
/// side, so this pipe-delimited string is the one channel the tagger controls end to end.
/// </summary>
/// <remarks>
/// <para>
/// Identifies a <b>line</b>, not an individual lens. Classic CodeLens follows the Roslyn model —
/// one <see cref="Microsoft.VisualStudio.Language.CodeLens.ICodeLensTag"/> marks one location, and
/// every registered provider contributes its own indicator to that single location (which is how a
/// C# method line shows "N references | N changes" side by side). A Scenario: line carries two lens
/// kinds (own-level hooks and, from <c>HookCodeLensHandler.AddStepHooksLens</c>, the scenario's
/// step-level hooks), so the two are rendered by two providers sharing one tag —
/// <see cref="HookCodeLensDataPointProvider"/> and <see cref="StepHooksCodeLensDataPointProvider"/> —
/// each filtering the server's response to its own kind in
/// <see cref="HookCodeLensDataPoint.GetDataAsync"/>. Emitting two tags for one line instead (issue
/// #400 live-test rounds 1-4) never worked: the engine renders a single adornment row per line and
/// resolves it against one location, so the second tag was always dropped regardless of how its
/// span or description differed.
/// </para>
/// <para>
/// The trailing <c>revision</c> field is opaque to the providers and exists only so the descriptor
/// changes whenever the line's lens content changes. <see cref="HookCodeLensTagger"/> reuses a tag
/// instance while its <c>ElementDescription</c> is unchanged; without a content-derived component
/// the description would be a bare line number, never change, and a refreshed count would never
/// reach the editor.
/// </para>
/// </remarks>
internal static class HookElementDescription
{
    /// <summary>Encodes the descriptor for <paramref name="line"/>, covering every lens entry the server reported on it.</summary>
    public static string Encode(int line, IEnumerable<HookFeatureLensEntry> entriesOnLine)
    {
        var revision = string.Join(";", entriesOnLine
            .OrderBy(e => e.AlwaysShowPicker).ThenBy(e => e.NavLine).ThenBy(e => e.NavChar)
            .Select(e => string.Join(",",
                e.Title,
                e.NavLine.ToString(CultureInfo.InvariantCulture),
                e.NavChar.ToString(CultureInfo.InvariantCulture),
                e.OwnLevelOnly ? "1" : "0",
                e.AlwaysShowPicker ? "1" : "0")));

        return string.Concat(line.ToString(CultureInfo.InvariantCulture), "|", revision);
    }

    /// <summary>
    /// Decodes the 0-based line a descriptor refers to. The remainder is the opaque revision
    /// component (see the class remarks) and is deliberately not surfaced — a data point resolves
    /// its own lens kind from a live server fetch, not from the descriptor.
    /// </summary>
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
