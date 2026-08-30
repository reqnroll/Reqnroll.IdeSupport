#nullable disable

using System;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>TagLinkConfiguration</summary>
public class TagLinkConfiguration
{
    /// <summary>Gets or sets the tag pattern.</summary>
    public string TagPattern { get; set; }
    /// <summary>Gets or sets the url template.</summary>
    public string UrlTemplate { get; set; }

    internal Regex ResolvedTagPattern { get; private set; }

    private void FixEmptyContainers()
    {
        //nop;
    }

    /// <summary>Validates this configuration and compiles <see cref="TagPattern"/> into <see cref="ResolvedTagPattern"/>.</summary>
    public void CheckConfiguration()
    {
        FixEmptyContainers();

        if (string.IsNullOrEmpty(TagPattern))
            throw new IdeSupportConfigurationException("'traceability/tagLinks[]/tagPattern' must be specified");
        if (string.IsNullOrEmpty(UrlTemplate))
            throw new IdeSupportConfigurationException("'traceability/tagLinks[]/urlTemplate' must be specified");

        try
        {
            ResolvedTagPattern = new Regex("^" + TagPattern.TrimStart('^').TrimEnd('$') + "$");
        }
        catch (Exception e)
        {
            throw new IdeSupportConfigurationException(
                $"Invalid regular expression '{TagPattern}' was specified as 'traceability/tagLinks[]/tagPattern': {e.Message}");
        }
    }

    #region Equality

    /// <summary>Determines whether this instance has the same setting values as <paramref name="other"/>.</summary>
    protected bool Equals(TagLinkConfiguration other) => string.Equals(TagPattern, other.TagPattern) &&
                                                         string.Equals(UrlTemplate, other.UrlTemplate) &&
                                                         Equals(ResolvedTagPattern, other.ResolvedTagPattern);

    /// <summary>Determines whether <paramref name="obj"/> is a <see cref="TagLinkConfiguration"/> with the same setting values.</summary>
    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((TagLinkConfiguration) obj);
    }

    /// <summary>Returns a hash code derived from the configuration's setting values.</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = TagPattern != null ? TagPattern.GetHashCode() : 0;
            hashCode = (hashCode * 397) ^ (UrlTemplate != null ? UrlTemplate.GetHashCode() : 0);
            hashCode = (hashCode * 397) ^ (ResolvedTagPattern != null ? ResolvedTagPattern.GetHashCode() : 0);
            return hashCode;
        }
    }

    #endregion
}
