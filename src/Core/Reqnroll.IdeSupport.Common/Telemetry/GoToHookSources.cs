namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// The closed set of values of the <see cref="PropertyName"/> property on the client-originated
/// <see cref="TelemetryEvents.GoToHookCommandExecuted"/> event (issue #861): how the user started
/// the navigation. Identical in Visual Studio, VS Code (<c>GoToHookSource</c> in
/// <c>telemetryEvents.ts</c>) and Rider (<c>RiderTelemetryTransmitter</c>) — keep the three in sync.
/// </summary>
public static class GoToHookSources
{
    /// <summary>
    /// The event property carrying the source (PascalCase on the wire, like every other property).
    /// Event-scoped: the LSP server's <c>TelemetryProperties.Source</c> uses the same literal for the
    /// class name on <c>UnhandledException</c>. Keep in step with <c>TelemetryProperties.source</c>
    /// (<c>telemetryEvents.ts</c>) and Rider's <c>GO_TO_HOOK_SOURCE_PROPERTY</c>.
    /// </summary>
    public const string PropertyName = "Source";

    /// <summary>A keybinding, command palette / action search, or a menu command with no editor-menu context.</summary>
    public const string Command = "Command";

    /// <summary>The editor right-click context menu.</summary>
    public const string ContextMenu = "ContextMenu";

    /// <summary>A click on the hook-count CodeLens / code vision entry.</summary>
    public const string CodeLens = "CodeLens";
}
