using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Privacy helpers for telemetry payloads: any string derived from an exception or other
/// user-influenced data must pass through here before it reaches <see cref="ILspTelemetryService"/>.
/// </summary>
public static class TelemetryScrubber
{
    // Windows absolute/UNC paths (C:\..., \server\share\...) and POSIX absolute paths (/home/...).
    // Deliberately broad (over-redacting is safe; under-redacting leaks a path) — see
    // docs/LSP-IDE-Support-Architecture.md's Privacy Considerations: "The Error event must scrub
    // exception messages for file paths and user-identifiable strings before transmission."
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:\\|\\\\|/)[^\s""'<>:*?|]+",
        RegexOptions.Compiled);

    /// <summary>Replaces filesystem-path-shaped substrings with <c>&lt;path&gt;</c>.</summary>
    public static string? RedactPaths(string? message) =>
        string.IsNullOrEmpty(message) ? message : PathPattern.Replace(message, "<path>");
}
