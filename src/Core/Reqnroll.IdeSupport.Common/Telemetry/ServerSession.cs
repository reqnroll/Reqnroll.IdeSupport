using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// The id of one server process. The client generates a fresh one for every server it starts and passes it
/// as <c>--server-session-id</c>; the server stamps it as <c>SessionId</c> on every event it sends, and the
/// client puts the same value on its own lifecycle events as <c>ServerSessionId</c>. Because the client
/// chooses it before the process exists, an exit or start failure joins to the dead run's events by equality
/// even when that run never sent anything. Random, not user-identifying. VS Code and Rider use the same
/// flag and property name.
/// </summary>
public static class ServerSession
{
    /// <summary>Property name on the client's lifecycle events; mirrored by the VS Code and Rider clients.</summary>
    public const string PropertyName = "ServerSessionId";

    /// <summary>Command-line flag the server reads its session id from; mirrored by the VS Code and Rider clients.</summary>
    public const string ServerArgumentName = "--server-session-id";

    /// <summary>A new id, in canonical <c>D</c> GUID form.</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Client-supplied text is only trusted as an id when it parses as a GUID; returns the canonical
    /// <c>D</c> form, or <see langword="null"/> otherwise.
    /// </summary>
    public static string? Normalize(string? value)
        => Guid.TryParse(value, out var guid) ? guid.ToString("D") : null;
}
