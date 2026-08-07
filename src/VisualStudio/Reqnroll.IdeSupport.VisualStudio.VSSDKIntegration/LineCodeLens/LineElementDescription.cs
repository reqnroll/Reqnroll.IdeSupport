#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Encodes/decodes what a <see cref="LineKeyedCodeLensTagger{TEntry}"/> smuggles through
/// <c>ICodeLensDescriptor.ElementDescription</c> to the out-of-process data-point providers — the
/// classic CodeLens remoting contract carries no buffer/span access into the data-point-provider
/// side, so this pipe-delimited string is the one channel a tagger controls end to end.
/// </summary>
/// <remarks>
/// Extracted from what was originally two near-identical copies (<c>HookElementDescription</c>,
/// <c>RunElementDescription</c>) — the "line index + opaque revision string" envelope is shared;
/// each feature still builds its own revision-key strings (its own notion of "what changed"), it
/// just hands them to <see cref="Encode"/> pre-formatted rather than reimplementing the envelope.
/// The trailing revision component is opaque to data-point providers by design and exists only so
/// the descriptor changes whenever a line's entry content changes — a tagger reuses its previous tag
/// instance while <c>ElementDescription</c> is unchanged (see <see cref="LineKeyedCodeLensTagger{TEntry}"/>),
/// so without a content-derived component a refreshed value would never reach the editor.
/// </remarks>
internal static class LineElementDescription
{
    /// <summary>Encodes the descriptor for <paramref name="line"/> from its already-formatted, already-ordered revision-key strings.</summary>
    public static string Encode(int line, IEnumerable<string> revisionKeys) =>
        string.Concat(line.ToString(CultureInfo.InvariantCulture), "|", string.Join(";", revisionKeys));

    /// <summary>
    /// Decodes the 0-based line a descriptor refers to. The revision component is deliberately not
    /// surfaced — a data point resolves its own entries from a live callback fetch, not from the
    /// descriptor.
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
