package com.reqnroll.ide.rider.telemetry

import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.extensions.PluginId
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
 * Port of the decision logic in VS's `WelcomeService.OnIdeScopeActivityStarted` (issue #875):
 * `Extension loaded` on every start, `Extension installed` on the first ever, `Extension upgraded`
 * when the plugin version increased, and a `"{N} day usage"` heartbeat on the first start of each
 * new local day (not on the install day, like VS). Pure: store and sink are injected.
 */
class ExtensionLifecycleTelemetry(
    private val store: LifecycleStore,
    private val send: (eventName: String, properties: Map<String, Any?>) -> Unit,
) {
    fun report(today: LocalDate, currentVersion: String) {
        send(RiderTelemetryTransmitter.EXTENSION_LOADED, emptyMap())

        val installedVersion = store.get(INSTALLED_VERSION_KEY)
        if (installedVersion.isNullOrEmpty()) {
            send(RiderTelemetryTransmitter.EXTENSION_INSTALLED, emptyMap())
            store.set(INSTALLED_VERSION_KEY, currentVersion)
            store.set(LAST_USED_DATE_KEY, today.toString())
            store.set(USAGE_DAYS_KEY, "0")
            return
        }

        if (store.get(LAST_USED_DATE_KEY) != today.toString()) {
            val usageDays = (store.get(USAGE_DAYS_KEY)?.toIntOrNull() ?: 0) + 1
            store.set(USAGE_DAYS_KEY, usageDays.toString())
            store.set(LAST_USED_DATE_KEY, today.toString())
            send(daysOfUsageEventName(usageDays), emptyMap())
        }

        if (compareVersions(installedVersion, currentVersion) < 0) {
            send(
                RiderTelemetryTransmitter.EXTENSION_UPGRADED,
                mapOf(RiderTelemetryTransmitter.OLD_EXTENSION_VERSION_PROPERTY to installedVersion),
            )
            store.set(INSTALLED_VERSION_KEY, currentVersion)
        }
    }

    companion object {
        internal const val INSTALLED_VERSION_KEY = "net.reqnroll.idesupport.telemetry.installedVersion"
        internal const val LAST_USED_DATE_KEY = "net.reqnroll.idesupport.telemetry.lastUsedDate"
        internal const val USAGE_DAYS_KEY = "net.reqnroll.idesupport.telemetry.usageDays"

        /** Mirrors `TelemetryEvents.DaysOfUsageEventNameFormat` (`"{0} day usage"`). */
        internal fun daysOfUsageEventName(usageDays: Int) = "$usageDays day usage"

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
 * Reports the extension lifecycle once per IDE process (the activity runs per opened project, so
 * only the first one counts), persisting state in the application-level [PropertiesComponent].
 */
class ReqnrollExtensionLifecycleActivity : ProjectActivity {
    override suspend fun execute(project: Project) {
        if (!reported.compareAndSet(false, true)) return
        try {
            val version = PluginManagerCore.getPlugin(PluginId.getId("net.reqnroll.idesupport"))?.version ?: return
            val properties = PropertiesComponent.getInstance()
            val store = object : LifecycleStore {
                override fun get(key: String): String? = properties.getValue(key)
                override fun set(key: String, value: String) = properties.setValue(key, value)
            }
            ExtensionLifecycleTelemetry(store, RiderTelemetryTransmitter::transmit).report(LocalDate.now(), version)
        } catch (ex: Exception) {
            // Telemetry must never break the plugin.
        }
    }

    private companion object {
        val reported = AtomicBoolean(false)
    }
}
