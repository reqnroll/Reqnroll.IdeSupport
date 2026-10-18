using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// The id of this IDE session as the client sees it (issue #1199): one random GUID per process of the IDE
/// that hosts the extension. Unlike the server's own <c>SessionId</c> (a server-process id that changes
/// whenever the client restarts the server) it exists before the server does and outlives it. The client
/// stamps it as <c>ClientSessionId</c> on every event it transmits and passes it to the server as
/// <c>--client-session-id</c>, which stamps it on the server's events, so a client-originated
/// <c>UnhandledException</c> joins to <c>ServerSessionStarted</c> (IDE, OS, versions). Random, not user-identifying.
/// VS Code and Rider generate their own and use the same property name and command-line flag.
/// </summary>
public static class ClientSession
{
    /// <summary>Telemetry property name; mirrored by the VS Code and Rider clients.</summary>
    public const string PropertyName = "ClientSessionId";

    /// <summary>Command-line flag the server reads the id from; mirrored by the VS Code and Rider clients.</summary>
    public const string ServerArgumentName = "--client-session-id";

    /// <summary>The id for this process, in canonical <c>D</c> GUID form.</summary>
    public static string Id { get; } = Guid.NewGuid().ToString("D");
}
