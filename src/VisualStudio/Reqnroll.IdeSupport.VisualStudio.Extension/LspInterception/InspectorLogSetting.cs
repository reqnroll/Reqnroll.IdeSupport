#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Decides whether the client-side LSP inspector log is written. The log records every message in
/// both directions and flushes each write, so it is off unless asked for.
/// </summary>
/// <remarks>
/// Mirrors the VS Code <c>reqnroll.trace.server</c> setting: <c>off</c> (the default) writes nothing,
/// <c>messages</c> and <c>verbose</c> turn the log on. The Visual Studio log always contains the full
/// message bodies, so the two enabled values behave the same here.
/// </remarks>
internal static class InspectorLogSetting
{
    /// <summary>Environment variable read when the server process starts.</summary>
    internal const string EnvironmentVariable = "REQNROLLVS_TRACE_SERVER";

    /// <summary>True when <see cref="EnvironmentVariable"/> turns the log on.</summary>
    internal static bool IsEnabled() => IsEnabledValue(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// The rule behind <see cref="IsEnabled()"/>: on for <c>messages</c>, <c>verbose</c>, <c>1</c> or
    /// <c>true</c> (case-insensitive); off for anything else, including an unrecognised value.
    /// </summary>
    internal static bool IsEnabledValue(string? value) =>
        value is not null &&
        (value.Trim().Equals("messages", StringComparison.OrdinalIgnoreCase)
         || value.Trim().Equals("verbose", StringComparison.OrdinalIgnoreCase)
         || value.Trim().Equals("1", StringComparison.Ordinal)
         || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
}
