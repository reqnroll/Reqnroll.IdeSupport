#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Factory that builds an <see cref="ITelemetryDebugLog"/> from the
/// <c>REQNROLL_TELEMETRY_DEBUG_LOG</c> environment variable. This is separate from
/// <c>REQNROLL_TELEMETRY_ENABLED</c> (the transmission opt-out): the debug log is a developer aid
/// and is <b>off by default</b>, regardless of whether telemetry transmission is enabled.
/// </summary>
public static class TelemetryDebugLog
{
    /// <summary>Name of the environment variable that configures the telemetry debug log sink.</summary>
    public const string EnvironmentVariable = "REQNROLL_TELEMETRY_DEBUG_LOG";

    /// <summary>
    /// Resolves the sink from the environment:
    /// <list type="bullet">
    ///   <item>unset / empty / <c>"0"</c> / <c>"false"</c> → disabled (no-op);</item>
    ///   <item><c>"1"</c> / <c>"true"</c> → JSONL at <see cref="DefaultPath"/>;</item>
    ///   <item>any other value → treated as the target file path.</item>
    /// </list>
    /// </summary>
    public static ITelemetryDebugLog FromEnvironment()
        => FromValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static ITelemetryDebugLog FromValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return NullTelemetryDebugLog.Instance;

        value = value!.Trim();
        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return NullTelemetryDebugLog.Instance;

        var path = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? DefaultPath()
            : value;

        return new FileTelemetryDebugLog(path);
    }

    /// <summary>
    /// <c>&lt;Reqnroll log dir&gt;\reqnroll-telemetry-{yyyyMMdd}.jsonl</c> (UTC date) — a sibling
    /// of the existing diagnostic logs written by <see cref="SynchronousFileLogger"/>, in the same
    /// <see cref="ReqnrollLogPaths.ResolveLogDirectory"/> directory.
    /// </summary>
    public static string DefaultPath()
        => Path.Combine(
            ReqnrollLogPaths.ResolveLogDirectory(),
            $"reqnroll-telemetry-{DateTime.UtcNow:yyyyMMdd}.jsonl");
}

/// <summary>
/// Appends telemetry records as newline-delimited JSON (one object per line) to a local file.
/// Append-only and lock-guarded; all I/O errors are swallowed so telemetry debugging never
/// affects the feature path.
/// </summary>
public sealed class FileTelemetryDebugLog : ITelemetryDebugLog
{
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="FileTelemetryDebugLog"/> class.</summary>
    public FileTelemetryDebugLog(string path)
    {
        _path = path;
    }

    /// <summary>Gets whether a sink is configured; always <c>true</c> since this instance is only created when a target path is set.</summary>
    public bool IsEnabled => true;

    /// <summary>Appends one JSON-line record describing the telemetry event to the log file.</summary>
    public void Record(string source, string eventName, object? properties,
        bool? enabled = null, bool? transmitted = null, string? error = null)
    {
        try
        {
            var line = JsonConvert.SerializeObject(new
            {
                ts = DateTime.UtcNow,
                source,
                @event = eventName,
                props = properties,
                enabled,
                transmitted,
                error
            });

            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Debug logging must never break the extension.
            Debug.WriteLine($"Error writing telemetry debug log to {_path}: {ex.Message}");
        }
    }
}
