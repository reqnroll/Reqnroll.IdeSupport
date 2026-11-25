#nullable enable

using System;
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
/// <para>
/// The envelope is <c>line|kinds|revision</c>, shared by the hook and Run lenses; each feature still
/// builds its own revision-key strings (its own notion of "what changed"), it just hands them to
/// <see cref="Encode"/> pre-formatted rather than reimplementing the envelope.
/// </para>
/// <para>
/// The <c>kinds</c> component lists which lens kinds (see <see cref="LineLensKinds"/>) the line
/// carries. The classic CodeLens host offers a line's tag to every registered data-point provider,
/// and a provider that creates a data point always gets an indicator slot in the row —
/// even when its data point later reports an empty description, the row still shows the slot and its
/// separator. Providers therefore check <see cref="HasLensKind"/> in <c>CanCreateDataPointAsync</c>
/// and decline descriptors that don't carry their kind, so no empty indicator is ever created.
/// </para>
/// <para>
/// The trailing revision component is opaque to data-point providers by design and exists only so
/// the descriptor changes whenever a line's entry content changes — a tagger reuses its previous tag
/// instance while <c>ElementDescription</c> is unchanged (see <see cref="LineKeyedCodeLensTagger{TEntry}"/>),
/// so without a content-derived component a refreshed value would never reach the editor. It may
/// itself contain <c>|</c>; only the first two separators are structural.
/// </para>
/// </remarks>
internal static class LineElementDescription
{
    private const char Separator = '|';
    private const char KindSeparator = ',';

    /// <summary>
    /// Encodes the descriptor for <paramref name="line"/> from the lens kinds it carries and its
    /// already-formatted, already-ordered revision-key strings.
    /// </summary>
    public static string Encode(int line, IEnumerable<string> lensKinds, IEnumerable<string> revisionKeys) =>
        string.Concat(
            line.ToString(CultureInfo.InvariantCulture),
            Separator.ToString(),
            string.Join(KindSeparator.ToString(), lensKinds.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)),
            Separator.ToString(),
            string.Join(";", revisionKeys));

    /// <summary>
    /// Decodes the 0-based line a descriptor refers to. The revision component is deliberately not
    /// surfaced — a data point resolves its own entries from a live callback fetch, not from the
    /// descriptor.
    /// </summary>
    public static bool TryDecode(string? elementDescription, out int line)
    {
        line = 0;

        var separator = elementDescription?.IndexOf(Separator) ?? -1;
        if (elementDescription is null || separator < 0)
            return false;

        return int.TryParse(
            elementDescription.Substring(0, separator),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out line);
    }

    /// <summary>
    /// Whether the descriptor decodes to a line and lists <paramref name="lensKind"/> among the lens
    /// kinds that line carries.
    /// </summary>
    public static bool HasLensKind(string? elementDescription, string lensKind)
    {
        if (!TryDecode(elementDescription, out _))
            return false;

        var kindsStart = elementDescription!.IndexOf(Separator) + 1;
        var kindsEnd = elementDescription.IndexOf(Separator, kindsStart);
        if (kindsEnd < 0)
            return false;

        return elementDescription
            .Substring(kindsStart, kindsEnd - kindsStart)
            .Split(KindSeparator)
            .Contains(lensKind, StringComparer.Ordinal);
    }
}
