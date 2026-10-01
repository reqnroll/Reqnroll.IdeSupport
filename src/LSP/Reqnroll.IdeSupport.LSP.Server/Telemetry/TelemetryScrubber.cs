using System.Collections.Generic;
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

    // Free-text properties that carry exception-derived text. Scrubbed by key (not every string
    // value) because the path pattern would also mangle legitimate values such as LSP method names
    // ("textDocument/definition").
    private static readonly string[] FreeTextKeys = ["ErrorMessage", "Message"];

    /// <summary>
    /// Returns <paramref name="properties"/> with filesystem paths redacted from the free-text
    /// properties (<c>ErrorMessage</c>, <c>Message</c>). Called at the last hop before the event
    /// leaves the server, so local logs (the telemetry debug-log mirror) keep the raw text.
    /// The input dictionary is never mutated.
    /// </summary>
    public static Dictionary<string, object?> ScrubProperties(Dictionary<string, object?> properties)
    {
        Dictionary<string, object?>? copy = null;
        foreach (var key in FreeTextKeys)
        {
            if (properties.TryGetValue(key, out var value) && value is string text)
            {
                copy ??= new Dictionary<string, object?>(properties);
                copy[key] = RedactPaths(text);
            }
        }
        return copy ?? properties;
    }
}
