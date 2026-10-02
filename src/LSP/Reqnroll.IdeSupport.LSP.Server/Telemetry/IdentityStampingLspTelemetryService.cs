#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Decorates <see cref="ILspTelemetryService"/> so every server-originated event carries one
/// canonical client identity, regardless of which IDE forwards it (issue #844). Stamping here, in
/// the server, keeps the three host forwarders dumb and guarantees identical behavior across them.
/// <para>
/// Stamped keys (<c>IdeClient</c>, <c>ServerVersion</c>, <c>SessionId</c>)
/// are added only when the caller has not already supplied the same key, and the caller's
/// dictionary is never mutated — a copy is forwarded. Null identity values (client not yet
/// initialized) are omitted rather than sent as empty strings.
/// </para>
/// </summary>
public sealed class IdentityStampingLspTelemetryService : ILspTelemetryService
{
    /// <summary>Canonical IDE identifier key (<c>visualstudio</c>/<c>vscode</c>/<c>rider</c>).</summary>
    public const string IdeClientKey = "IdeClient";

    /// <summary>The LSP server's informational version.</summary>
    public const string ServerVersionKey = "ServerVersion";

    /// <summary>Random per-server-process identifier; not user-identifying.</summary>
    public const string SessionIdKey = "SessionId";

    private readonly ILspTelemetryService _inner;
    private readonly ClientIdeContext _ide;
    private readonly string _serverVersion;
    private readonly string _sessionId;

    /// <summary>Initializes a new instance of the <see cref="IdentityStampingLspTelemetryService"/> class.</summary>
    public IdentityStampingLspTelemetryService(ILspTelemetryService inner, ClientIdeContext ide)
        : this(inner, ide, GetServerVersion(), Guid.NewGuid().ToString("D"))
    {
    }

    /// <summary>Test seam: supplies the server version and session id explicitly.</summary>
    internal IdentityStampingLspTelemetryService(
        ILspTelemetryService inner, ClientIdeContext ide, string serverVersion, string sessionId)
    {
        _inner = inner;
        _ide = ide;
        _serverVersion = serverVersion;
        _sessionId = sessionId;
    }

    /// <summary>Forwards the event with the canonical identity keys added where absent.</summary>
    public void SendEvent(string eventName, Dictionary<string, object?> properties)
    {
        var stamped = new Dictionary<string, object?>(properties);
        TryStamp(stamped, IdeClientKey, _ide.Ide);
        TryStamp(stamped, ServerVersionKey, _serverVersion);
        TryStamp(stamped, SessionIdKey, _sessionId);
        _inner.SendEvent(eventName, stamped);
    }

    private static void TryStamp(Dictionary<string, object?> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            properties.TryAdd(key, value);
    }

    private static string GetServerVersion()
    {
        var assembly = typeof(IdentityStampingLspTelemetryService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
