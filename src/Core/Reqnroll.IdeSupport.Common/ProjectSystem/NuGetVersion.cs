#nullable disable

using System;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Common.ProjectSystem;

/// <summary>NuGetVersion</summary>
public record NuGetVersion
{
    /// <summary>Initializes a new instance of the <see cref="NuGetVersion"/> class.</summary>
    public NuGetVersion(string versionSpecifier, string requestedRange)
    {
        if (versionSpecifier == null) throw new ArgumentNullException(nameof(versionSpecifier));
        RequestedRange = requestedRange;

        var versionParts = versionSpecifier.Split(new[] {'-'}, 2);
        if (Version.TryParse(versionParts[0], out var version))
            Version = version;
        else
            Version = new Version();
        if (versionParts.Length > 1)
            PreReleaseSuffix = versionParts[1];
    }

    /// <summary>Gets the parsed numeric version (excluding any pre-release suffix).</summary>
    public Version Version { get; }
    /// <summary>Gets the pre-release suffix (the part after the first <c>-</c>), or <c>null</c> if this is not a pre-release version.</summary>
    public string PreReleaseSuffix { get; }
    /// <summary>Gets whether this version has a pre-release suffix.</summary>
    public bool IsPrerelease => PreReleaseSuffix != null;

    /// <summary>The project's requested package range for the package.</summary>
    /// <remarks>
    ///     If the project uses packages.config, this will be same as the installed package version.
    ///     If the project uses PackageReference, this is the version string in the project file, which may not match the
    ///     resolved package version, and may not be single version string.
    ///     If the project uses PackageReference, and the package is a transitive dependency, the value will be null.
    /// </remarks>
    public string RequestedRange { get; }

    /// <summary>Gets whether <see cref="RequestedRange"/> uses a floating version pattern (contains <c>*</c>).</summary>
    public bool IsFloating => RequestedRange != null && Regex.IsMatch(RequestedRange, @"\*");

    /// <summary>Returns the version as a string, including the pre-release suffix and requested range where applicable.</summary>
    public override string ToString()
    {
        var str = IsPrerelease ? $"{Version}-{PreReleaseSuffix}" : Version.ToString();
        if (IsFloating) str += $"({RequestedRange})";
        return str;
    }
}
