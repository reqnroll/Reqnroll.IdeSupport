package net.reqnroll.idesupport.rider.telemetry

import java.util.UUID

/**
 * The id of this Rider session as the plugin sees it: one random GUID per IDE process. It is
 * stamped as `ClientSessionId` on every event the plugin transmits ([RiderTelemetryTransmitter]) and passed to
 * the LSP server as `--client-session-id`, which stamps it on the server's events too, so a client-originated
 * `UnhandledException` joins to `ServerSessionStarted`. Mirrors `ClientSession` in `Reqnroll.IdeSupport.Common`
 * and the VS Code extension's `clientSession.ts`. Random, not user-identifying.
 */
object ClientSession {
    /** Telemetry property name. */
    const val PROPERTY_NAME = "ClientSessionId"

    /** Server command-line flag. */
    const val SERVER_ARGUMENT_NAME = "--client-session-id"

    val id: String = UUID.randomUUID().toString()
}
