package net.reqnroll.idesupport.rider

import com.intellij.openapi.application.PathManager
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.jar.JarFile

/**
 * Where this plugin is installed and which version it is, found from the location of its own
 * classes. The platform's plugin-descriptor lookups are internal API (and the Marketplace rejects
 * plugins that use it), so this reads the installed layout instead: `<plugin dir>/lib/<plugin>.jar`
 * holding `META-INF/plugin.xml`.
 */
object ReqnrollPlugin {
    class Descriptor(val pluginPath: Path, val version: String?)

    private val versionTag = Regex("""<version>\s*([^<\s]+)\s*</version>""")

    /** The plugin's own `<version>` from the text of its `plugin.xml`, or null if it has none. */
    internal fun versionIn(pluginXml: String): String? = versionTag.find(pluginXml)?.groupValues?.get(1)

    // Resolved once: the telemetry transmitter asks for the version on every event.
    private val resolved: Descriptor? by lazy { resolve() }

    fun descriptor(): Descriptor? = resolved

    private fun resolve(): Descriptor? {
        val jar = PathManager.getJarPathForClass(ReqnrollPlugin::class.java)?.let { Paths.get(it) } ?: return null
        val pluginDir = jar.parent?.parent ?: return null
        return Descriptor(pluginDir, readVersion(jar))
    }

    private fun readVersion(jar: Path): String? = try {
        val pluginXml = if (Files.isDirectory(jar)) {
            jar.resolve("META-INF/plugin.xml").takeIf { Files.isRegularFile(it) }?.let { Files.readString(it) }
        } else {
            JarFile(jar.toFile()).use { file ->
                file.getEntry("META-INF/plugin.xml")?.let { file.getInputStream(it).readBytes().decodeToString() }
            }
        }
        pluginXml?.let(::versionIn)
    } catch (e: java.io.IOException) {
        null
    }
}
