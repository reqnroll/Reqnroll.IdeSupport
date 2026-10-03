using System;
using System.Linq;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>TraceabilityConfiguration</summary>
public class TraceabilityConfiguration
{
    /// <summary>Gets or sets the tag links.</summary>
    public TagLinkConfiguration[] TagLinks { get; set; } = new TagLinkConfiguration[0];

    private void FixEmptyContainers()
    {
        TagLinks = TagLinks ?? new TagLinkConfiguration[0];
    }

    /// <summary>Validates and normalizes this configuration and each of its tag link entries.</summary>
    public void CheckConfiguration()
    {
        FixEmptyContainers();

        foreach (var tagLinkConfiguration in TagLinks) tagLinkConfiguration.CheckConfiguration();
    }

    /// <summary>
    /// Resolves the link target for a Gherkin tag (with or without the leading <c>@</c>) using the first
    /// configured tag link whose pattern matches, or returns null when none match.
    /// </summary>
    public Uri ResolveTagLink(string tagName)
    {
        if (string.IsNullOrEmpty(tagName) || TagLinks == null)
            return null;

        var name = tagName.TrimStart('@');
        foreach (var tagLink in TagLinks)
        {
            var url = tagLink.ResolveUrl(name);
            if (url != null)
                return url;
        }

        return null;
    }

    #region Equality

    /// <summary>Determines whether this instance has the same tag links as <paramref name="other"/>.</summary>
    protected bool Equals(TraceabilityConfiguration other) => Equals(TagLinks, other.TagLinks);

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="TraceabilityConfiguration"/> with the same tag links.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((TraceabilityConfiguration) obj);
    }

    /// <summary>Returns a hash code derived from the tag links array.</summary>
    public override int GetHashCode() => TagLinks != null ? TagLinks.GetHashCode() : 0;

    #endregion
}
