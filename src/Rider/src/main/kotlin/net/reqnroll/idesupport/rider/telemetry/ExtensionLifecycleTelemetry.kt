package net.reqnroll.idesupport.rider.telemetry

import net.reqnroll.idesupport.rider.lsp.ReqnrollServerEvents
import com.intellij.openapi.components.service
import net.reqnroll.idesupport.rider.ReqnrollPlugin
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import java.time.LocalDate
import java.util.concurrent.atomic.AtomicBoolean

/** Minimal string key/value store; [PropertiesComponent] in production, a map in tests. */
interface LifecycleStore {
    fun get(key: String): String?
    fun set(key: String, value: String)
}

/**
 * Port of the decision logic in VS's `WelcomeService`, in two halves. [reportInstallOrUpgrade] runs at
 * startup and is sent whether or not the solution uses Reqnroll: `Lifecycle.Extension.Installed` on the
 * first start ever (which also seeds the state, so the install day is not a usage day, like VS) and
 * `Lifecycle.Extension.Upgraded` when the plugin version increased. [reportUse] runs once the server
 * reports a project that uses Reqnroll: `Lifecycle.Extension.Loaded`, and a `Lifecycle.Usage.Recorded`
 * heartbeat (property `UsageDays`) on the first such report of each new local day. Pure: store and sink
 * are injected.
 */
class ExtensionLifecycleTelemetry(
    private val store: LifecycleStore,
    private val send: (eventName: String, properties: Map<String, Any?>) -> Unit,
) {
    fun reportInstallOrUpgrade(today: LocalDate, currentVersion: String) {
        val installedVersion = store.get(INSTALLED_VERSION_KEY)
        if (installedVersion.isNullOrEmpty()) {
            send(RiderTelemetryTransmitter.LIFECYCLE_EXTENSION_INSTALLED, emptyMap())
            store.set(INSTALLED_VERSION_KEY, currentVersion)
            store.set(LAST_USED_DATE_KEY, today.toString())
            store.set(USAGE_DAYS_KEY, "0")
            return
        }

        if (compareVersions(installedVersion, currentVersion) < 0) {
            send(
                RiderTelemetryTransmitter.LIFECYCLE_EXTENSION_UPGRADED,
                mapOf(RiderTelemetryTransmitter.OLD_EXTENSION_VERSION_PROPERTY to installedVersion),
            )
            store.set(INSTALLED_VERSION_KEY, currentVersion)
        }
    }

    fun reportUse(today: LocalDate) {
        send(RiderTelemetryTransmitter.LIFECYCLE_EXTENSION_LOADED, emptyMap())

        if (store.get(INSTALLED_VERSION_KEY).isNullOrEmpty()) return
        if (store.get(LAST_USED_DATE_KEY) != today.toString()) {
            val usageDays = (store.get(USAGE_DAYS_KEY)?.toIntOrNull() ?: 0) + 1
            store.set(USAGE_DAYS_KEY, usageDays.toString())
            store.set(LAST_USED_DATE_KEY, today.toString())
            send(RiderTelemetryTransmitter.LIFECYCLE_USAGE_RECORDED, mapOf(RiderTelemetryTransmitter.USAGE_DAYS_PROPERTY to usageDays))
        }
    }

    companion object {
        internal const val INSTALLED_VERSION_KEY = "net.reqnroll.idesupport.telemetry.installedVersion"
        internal const val LAST_USED_DATE_KEY = "net.reqnroll.idesupport.telemetry.lastUsedDate"
        internal const val USAGE_DAYS_KEY = "net.reqnroll.idesupport.telemetry.usageDays"

        /** Numeric dotted-version comparison; non-numeric segments count as 0. */
        internal fun compareVersions(a: String, b: String): Int {
            val pa = a.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
            val pb = b.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
            for (i in 0 until maxOf(pa.size, pb.size)) {
                val diff = (pa.getOrElse(i) { 0 }).compareTo(pb.getOrElse(i) { 0 })
                if (diff != 0) return diff
            }
            return 0
        }
    }
}

/**
 * Reports the extension lifecycle once per IDE process, persisting state in the application-level
 * [PropertiesComponent]. Install and upgrade are reported at the first project startup. `Loaded` and
 * the daily heartbeat wait until the first solution whose language server reports a project that uses
 * Reqnroll, whichever window that is, so a solution without Reqnroll is not counted as a day of use.
 * The activity runs per opened project, so each one arms its own solution's gate.
 */
class ReqnrollExtensionLifecycleActivity : ProjectActivity {
    override suspend fun execute(project: Project) {
        if (installReported.compareAndSet(false, true)) {
            ReqnrollPlugin.descriptor()?.version?.let { version ->
                withLifecycle { it.reportInstallOrUpgrade(LocalDate.now(), version) }
            }
        }
        project.service<ReqnrollServerEvents>().telemetryGate.onOpened {
            if (useReported.compareAndSet(false, true)) withLifecycle { it.reportUse(LocalDate.now()) }
        }
    }

    // Synchronized: the install/upgrade report and the use report read and rewrite the same keys,
    // and the second runs on the language-server thread when the notification arrives.
    private fun withLifecycle(action: (ExtensionLifecycleTelemetry) -> Unit) = synchronized(lock) {
        try {
            val properties = PropertiesComponent.getInstance()
            val store = object : LifecycleStore {
                override fun get(key: String): String? = properties.getValue(key)
                override fun set(key: String, value: String) = properties.setValue(key, value)
            }
            action(ExtensionLifecycleTelemetry(store, RiderTelemetryTransmitter::transmit))
        } catch (ex: Exception) {
            // Telemetry must never break the plugin.
        }
    }

    private companion object {
        val installReported = AtomicBoolean(false)
        val useReported = AtomicBoolean(false)
        val lock = Any()
    }
}
