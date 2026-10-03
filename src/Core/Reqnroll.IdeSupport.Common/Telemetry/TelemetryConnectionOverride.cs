using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// Developer override for the Application Insights connection string (issue #889), read from
/// <see cref="EnvironmentVariable"/>. Mirrored by VS Code's <c>telemetry.ts</c> and Rider's
/// <c>RiderTelemetryTransmitter.kt</c>.
/// </summary>
public static class TelemetryConnectionOverride
{
    /// <summary>Name of the environment variable holding an Application Insights connection string.</summary>
    public const string EnvironmentVariable = "REQNROLL_TELEMETRY_CONNECTION_STRING";

    /// <summary>
    /// Debug-build guard (issue #889): a Debug build must not send to the built-in (production)
    /// connection, so with no usable override its telemetry is not transmitted at all.
    /// </summary>
    public static bool BlocksBuiltIn(bool isDebugBuild, string? resolvedOverride)
        => isDebugBuild && resolvedOverride is null;

    /// <summary>
    /// Returns the override from the environment, or <see langword="null"/> when it is unset or not a
    /// usable connection string (in which case <paramref name="onInvalid"/> is told why).
    /// </summary>
    public static string? FromEnvironment(Action<string>? onInvalid = null)
        => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), onInvalid);

    /// <summary>Validates <paramref name="value"/>: it must carry a non-empty <c>InstrumentationKey</c>.</summary>
    public static string? Resolve(string? value, Action<string>? onInvalid = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value!.Trim();
        foreach (var part in trimmed.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0
                && part.Substring(0, eq).Trim().Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase)
                && part.Substring(eq + 1).Trim().Length > 0)
                return trimmed;
        }

        onInvalid?.Invoke($"{EnvironmentVariable} has no InstrumentationKey; ignoring it.");
        return null;
    }
}
