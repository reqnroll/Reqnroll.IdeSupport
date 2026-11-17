#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// The versions of the inputs a piece of per-document derived state (a match set, a tag set) was
/// built from: the document's text, its project's configuration, and the binding registry it was
/// matched against.
/// </summary>
/// <param name="ContentGeneration">
/// The document's content generation (see <c>IDocumentGenerations</c>), read before the text was read.
/// </param>
/// <param name="ConfigGeneration">
/// The owning project's configuration generation (see <c>IProjectConfigGenerations</c>), read before
/// the feature language was read. 0 when there is no owning project.
/// </param>
/// <param name="RegistryVersion">The version of the binding registry the document was matched against.</param>
/// <remarks>
/// Ordered lexicographically: newer text always wins; for the same text, the newer configuration
/// wins; for the same text and configuration, the newer registry wins. A total order, so two stamps
/// are never incomparable. Each input is read before the derivation uses it, so a derivation that
/// starts later never has an older component than one that started earlier, and the order of the
/// components only acts as a tie-break.
/// </remarks>
public readonly record struct DerivationStamp(long ContentGeneration, long ConfigGeneration, int RegistryVersion)
    : IComparable<DerivationStamp>
{
    /// <inheritdoc/>
    public int CompareTo(DerivationStamp other)
    {
        var byContent = ContentGeneration.CompareTo(other.ContentGeneration);
        if (byContent != 0) return byContent;

        var byConfig = ConfigGeneration.CompareTo(other.ConfigGeneration);
        return byConfig != 0 ? byConfig : RegistryVersion.CompareTo(other.RegistryVersion);
    }

    /// <summary>Whether <paramref name="left"/> was built from older inputs than <paramref name="right"/>.</summary>
    public static bool operator <(DerivationStamp left, DerivationStamp right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> was built from newer inputs than <paramref name="right"/>.</summary>
    public static bool operator >(DerivationStamp left, DerivationStamp right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> was built from the same or older inputs than <paramref name="right"/>.</summary>
    public static bool operator <=(DerivationStamp left, DerivationStamp right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> was built from the same or newer inputs than <paramref name="right"/>.</summary>
    public static bool operator >=(DerivationStamp left, DerivationStamp right) => left.CompareTo(right) >= 0;
}
