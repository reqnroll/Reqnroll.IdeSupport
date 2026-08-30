#nullable enable

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>No-op sink used when the debug log is not configured.</summary>
public sealed class NullTelemetryDebugLog : ITelemetryDebugLog
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly NullTelemetryDebugLog Instance = new();

    private NullTelemetryDebugLog() { }

    /// <summary>Gets whether a sink is configured; always <c>false</c> for this no-op implementation.</summary>
    public bool IsEnabled => false;

    /// <summary>No-op: discards the telemetry record.</summary>
    public void Record(string source, string eventName, object? properties,
        bool? enabled = null, bool? transmitted = null, string? error = null)
    {
        // no-op
    }
}
