package net.reqnroll.idesupport.rider.lsp

import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import org.eclipse.lsp4j.MessageParams
import org.eclipse.lsp4j.MessageType

/**
 * Writes the server's `reqnroll/logMessage` notifications (problems the user can act on, such as a
 * failed binding discovery) to the Reqnroll tool window through [ReqnrollDebugLogger]. The server
 * sends these instead of `window/logMessage`, which the platform's LSP client keeps to its own
 * language-server log.
 */
object ServerLogMessages {
    /** The [ReqnrollDebugLogger] level a server message of [type] is written at. Exposed for testing. */
    internal fun levelFor(type: MessageType?): String = when (type) {
        MessageType.Error -> "Error"
        MessageType.Warning -> "Warning"
        MessageType.Info -> "Info"
        else -> "Verbose"
    }

    /** Writes [params] at its level through [write]; a message without text is ignored. Exposed for testing. */
    internal fun write(params: MessageParams?, write: (level: String, message: String) -> Unit) {
        val message = params?.message
        if (message.isNullOrEmpty()) return
        write(levelFor(params.type), message)
    }

    /** Writes [params] to the Reqnroll tool window (and the plugin's log file). */
    fun write(params: MessageParams?) = write(params) { level, message ->
        when (level) {
            "Error" -> ReqnrollDebugLogger.error(message)
            "Warning" -> ReqnrollDebugLogger.warn(message)
            "Info" -> ReqnrollDebugLogger.info(message)
            else -> ReqnrollDebugLogger.verbose(message)
        }
    }
}
