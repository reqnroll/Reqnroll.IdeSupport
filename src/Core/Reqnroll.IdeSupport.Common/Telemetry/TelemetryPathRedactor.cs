using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Redacts URLs and filesystem paths from free text before it is transmitted as telemetry. Shared by the LSP
/// server (<c>TelemetryScrubber.RedactPaths</c>) and the Visual Studio host's exception transmitter (#1027), so
/// every .NET host applies the same rules.
/// </summary>
public static class TelemetryPathRedactor
{
    // Path- and URL-shaped substrings, redacted in this order (most to least specific). Deliberately broad
    // (over-redacting is safe; under-redacting leaks a path) — see docs/LSP-IDE-Support-Architecture.md's
    // Privacy Considerations: "The Error event must scrub exception messages for file paths and
    // user-identifiable strings before transmission." Mirrored by the VS Code and Rider clients'
    // exception scrubbers (issue #621), which additionally drop quoted text.
    // A directory segment may contain interior spaces ("C:\Users\John Smith\x") but never starts or ends
    // with one, so a path cannot swallow the words (or a second path) that follow it.
    private const string Segments = @"(?:[^\\/\s""'<>|*?:](?:[^\\/\r\n""'<>|*?:]*[^\\/\s""'<>|*?:])?[\\/])*";
    private const string LastSegment = @"[^\\/\s""'<>|*?:]*";
    private const string FileExtensions =
        "feature|cs|csproj|fsproj|vbproj|sln|slnx|slnf|json|md|xml|config|props|targets|txt|yml|yaml|ts|js|kt|java|ps1|sh|dll|exe|log|trx|runsettings|vb|fs|razor|cshtml|proj|projitems|shproj|nuspec|resx|ini|toml|lock";

    // URLs: dropped wholly, so a query string token or fragment never leaves.
    private static readonly Regex UrlPattern = new(
        @"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>]+",
        RegexOptions.Compiled);

    private static readonly Regex[] PathPatterns =
    [
        new(@"[A-Za-z]:[\\/]" + Segments + LastSegment, RegexOptions.Compiled), // drive-letter
        new(@"\\\\" + Segments + LastSegment, RegexOptions.Compiled), // UNC
        new(@"(?:~|%[A-Za-z_]\w*%|\$\{?[A-Za-z_]\w*\}?)[\\/]" + Segments + LastSegment, RegexOptions.Compiled), // ~, %VAR%, $VAR
        new(@"(?<![\w~%$}.])/(?=[^\s/])" + Segments + LastSegment, RegexOptions.Compiled), // POSIX absolute
        new(@"(?<![\w.])\.{1,2}[\\/][^\s""'<>|*?:]*", RegexOptions.Compiled), // ./x, ../x
        new(@"(?<!\w)(?:[\w.\-]+[\\/])*[\w.\-]+\.(?:" + FileExtensions + @")\b", RegexOptions.Compiled), // bare names / relative paths with a known extension
    ];

    /// <summary>
    /// Replaces URLs (<c>&lt;url&gt;</c>, query string and fragment included) and filesystem-path-shaped
    /// substrings (absolute, UNC, home/env-var, relative and bare file names with a source/config extension)
    /// with <c>&lt;path&gt;</c>.
    /// </summary>
    public static string? RedactPaths(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        var result = UrlPattern.Replace(message, "<url>");
        foreach (var pattern in PathPatterns)
            result = pattern.Replace(result, "<path>");
        return result;
    }
}
