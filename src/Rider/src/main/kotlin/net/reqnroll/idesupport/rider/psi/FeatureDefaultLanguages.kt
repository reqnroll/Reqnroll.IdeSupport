package net.reqnroll.idesupport.rider.psi

import com.google.gson.JsonParser
import com.intellij.openapi.vfs.VirtualFile
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.concurrent.ConcurrentHashMap

/**
 * Finds the Gherkin dialect a feature file is parsed with when it has no `# language:` header: the
 * `language.feature` setting of its project's `reqnroll.json` (or `specflow.json`), else English.
 *
 * The language server resolves the same setting from the project's loaded configuration; this reads the file
 * itself because the PSI is built synchronously, without a server round trip. It looks in the file's own
 * directory and each parent up to and including the first one that holds a project file, which is where the
 * server finds a project's configuration. `App.config` is not read.
 *
 * Results are cached per configuration file and invalidated when its modification time changes. Callers that
 * already hold a parsed tree must re-parse it themselves after the setting changes.
 */
internal object FeatureDefaultLanguages {
    private val configFileNames = listOf("reqnroll.json", "specflow.json")
    private val projectFileExtensions = setOf("csproj", "vbproj", "fsproj")

    private class Cached(val lastModified: Long, val language: String?)

    private val cache = ConcurrentHashMap<Path, Cached>()

    /** The default language for [file], or [FeatureSyntax.FALLBACK_LANGUAGE] when it cannot be determined. */
    fun languageFor(file: VirtualFile?): String =
        if (file == null || !file.isInLocalFileSystem) FeatureSyntax.FALLBACK_LANGUAGE else languageForPath(file.path)

    /** The default language for the feature file at [path], or [FeatureSyntax.FALLBACK_LANGUAGE]. */
    fun languageForPath(path: String): String {
        var directory: Path? = runCatching { Paths.get(path).parent }.getOrNull()
        while (directory != null) {
            for (name in configFileNames) {
                val config = directory.resolve(name)
                if (Files.isRegularFile(config)) {
                    languageIn(config)?.let { return it }
                }
            }
            if (holdsProjectFile(directory)) break
            directory = directory.parent
        }
        return FeatureSyntax.FALLBACK_LANGUAGE
    }

    private fun holdsProjectFile(directory: Path): Boolean = runCatching {
        Files.newDirectoryStream(directory).use { entries ->
            entries.any { it.fileName.toString().substringAfterLast('.', "").lowercase() in projectFileExtensions }
        }
    }.getOrDefault(false)

    private fun languageIn(config: Path): String? {
        val lastModified = runCatching { Files.getLastModifiedTime(config).toMillis() }.getOrNull() ?: return null
        cache[config]?.let { if (it.lastModified == lastModified) return it.language }
        val language = readLanguage(config)
        cache[config] = Cached(lastModified, language)
        return language
    }

    private fun readLanguage(config: Path): String? = runCatching {
        JsonParser.parseString(Files.readString(config)).asJsonObject
            .getAsJsonObject("language")
            ?.get("feature")
            ?.asString
            ?.trim()
            ?.takeIf { it.isNotEmpty() }
    }.getOrNull()
}
