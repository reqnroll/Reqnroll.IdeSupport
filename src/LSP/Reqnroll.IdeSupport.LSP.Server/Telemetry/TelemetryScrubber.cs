using System.Collections.Generic;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Privacy helpers for telemetry payloads: any string derived from an exception or other
/// user-influenced data must pass through here before it reaches <see cref="ILspTelemetryService"/>.
/// </summary>
public static class TelemetryScrubber
{
    /// <summary>
    /// Replaces URLs (<c>&lt;url&gt;</c>, query string and fragment included) and filesystem-path-shaped
    /// substrings (absolute, UNC, home/env-var, relative and bare file names with a source/config extension)
    /// with <c>&lt;path&gt;</c>. The rules live in <see cref="TelemetryPathRedactor"/> (Common) so the Visual
    /// Studio host applies the same redaction to its exception telemetry (#1027).
    /// </summary>
    public static string? RedactPaths(string? message) => TelemetryPathRedactor.RedactPaths(message);

    // Free-text properties that carry exception-derived text. Scrubbed by key (not every string
    // value) because the path pattern would also mangle legitimate values such as LSP method names
    // ("textDocument/definition").
    private static readonly string[] FreeTextKeys = ["ErrorMessage", "Message", TelemetryProperties.StackFrames];

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
