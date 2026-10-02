using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;

/// <summary>
/// The privacy-safe subset of an out-of-process connector run's
/// <c>DiscoveryResult.TelemetryProperties</c> that is merged into the
/// <c>ReqnrollDiscoveryExecuted</c> telemetry event (issue #846).
/// </summary>
/// <remarks>
/// The connector's own dictionary is deliberately *not* forwarded wholesale: it also holds
/// <c>ConnectorArguments</c> (the command line, which contains file-system paths) and the raw
/// <c>Error</c> text. This type is an explicit whitelist, so adding a key to the connector's
/// dictionary can never leak into telemetry by accident. Only versions (reduced to major.minor),
/// an enum-like connector type and an exit code are carried.
/// </remarks>
/// <param name="ReqnrollVersion">The Reqnroll (or SpecFlow) version the connector reported, normalised to <c>major.minor</c>; <see langword="null"/> when unknown.</param>
/// <param name="ConnectorType">Which connector flavour ran (e.g. generic or custom).</param>
/// <param name="ConnectorExitCode">The connector process exit code; <see langword="null"/> when unavailable.</param>
public sealed record ConnectorRunTelemetry(
    string? ReqnrollVersion,
    string? ConnectorType,
    int? ConnectorExitCode)
{
    /// <summary>Builds the whitelisted subset from the connector's raw telemetry dictionary.</summary>
    public static ConnectorRunTelemetry FromConnectorProperties(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties is null)
            return new ConnectorRunTelemetry(null, null, null);

        properties.TryGetValue("ReqnrollVersion", out var version);
        properties.TryGetValue("ConnectorType", out var connectorType);
        properties.TryGetValue("ConnectorExitCode", out var exitCode);

        return new ConnectorRunTelemetry(
            NormalizeVersion(AsString(version)),
            AsString(connectorType),
            exitCode switch
            {
                int i => i,
                JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var ei) => ei,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                _ => null,
            });
    }

    /// <summary>Adds the whitelisted keys to <paramref name="target"/>, omitting the ones that are unknown.</summary>
    public void AddTo(IDictionary<string, object?> target)
    {
        if (ReqnrollVersion is not null)
            target["ReqnrollVersion"] = ReqnrollVersion;
        if (ConnectorType is not null)
            target["ConnectorType"] = ConnectorType;
        if (ConnectorExitCode is not null)
            target["ConnectorExitCode"] = ConnectorExitCode;
    }

    /// <summary>
    /// Reads a string out of a connector telemetry value. The connector's JSON is deserialized with
    /// System.Text.Json into <c>Dictionary&lt;string, object&gt;</c>, so values that crossed the process
    /// boundary are <see cref="JsonElement"/>s, while values the server added itself are plain strings.
    /// </summary>
    internal static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private static readonly Regex MajorMinor = new(@"^\s*v?(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    /// <summary>
    /// Reduces a version string to <c>major.minor</c> (or <c>major</c> when no minor is present),
    /// dropping patch, pre-release and build metadata; <see langword="null"/> when unparseable.
    /// </summary>
    internal static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var m = MajorMinor.Match(version);
        if (!m.Success)
            return null;

        return m.Groups[2].Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : m.Groups[1].Value;
    }
}
