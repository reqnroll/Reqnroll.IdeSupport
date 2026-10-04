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

    // The pattern comes from repository configuration and runs against every tag on each documentLink
    // request, so a pathological pattern (e.g. "(a+)+") must not be able to hang the server.
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

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
            ResolvedTagPattern = new Regex("^" + TagPattern.TrimStart('^').TrimEnd('$') + "$", RegexOptions.None, MatchTimeout);
        }
        catch (Exception e)
        {
            throw new IdeSupportConfigurationException(
                $"Invalid regular expression '{TagPattern}' was specified as 'traceability/tagLinks[]/tagPattern': {e.Message}");
        }
    }

    /// <summary>
    /// Expands <see cref="UrlTemplate"/> with the named groups captured from <paramref name="tagName"/>
    /// (no leading <c>@</c>), or returns null when the pattern does not match or the result is not an absolute URI.
    /// </summary>
    public Uri ResolveUrl(string tagName)
    {
        if (ResolvedTagPattern == null || UrlTemplate == null)
            return null;

        Match match;
        try
        {
            match = ResolvedTagPattern.Match(tagName);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
            return null;

        var url = Regex.Replace(UrlTemplate, @"\{(?<paramName>[a-zA-Z_\d]+)\}",
            paramMatch => match.Groups[paramMatch.Groups["paramName"].Value].Value);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
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
