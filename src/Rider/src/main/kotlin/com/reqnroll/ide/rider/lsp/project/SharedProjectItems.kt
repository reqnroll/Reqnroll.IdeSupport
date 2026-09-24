package com.reqnroll.ide.rider.lsp.project

import org.w3c.dom.Element
import java.io.File
import java.io.IOException
import javax.xml.XMLConstants
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Lists the files a project compiles from the shared projects it imports — the items of every
 * `.projitems` named by one of its `<Import>` elements (issue #736).
 *
 * A shared project's sources live outside the importing project's folder by definition, so
 * [ReqnrollProjectBaseline.sendProjectFilesBaseline]'s folder walk never finds them; this adds
 * them to the importing project's baseline, which is where they actually compile.
 *
 * A Kotlin port of `Reqnroll.IdeSupport.Common.ProjectSystem.SharedProjectItems` (used by the VS
 * extension) — keep the two in step. Deliberately a small reading of MSBuild, not an evaluation:
 * it resolves only `$(MSBuildThisFileDirectory)`, `$(MSBuildThisFileFullPath)` and
 * `$(MSBuildProjectDirectory)`, skips any path still naming another property, honours
 * `Include`/`Exclude`/`Remove` with `*`, `?` and `**` wildcards, and ignores `Condition`s. A file
 * it cannot read contributes nothing rather than throwing.
 */
object SharedProjectItems {
    // Same list the VS Code client asks `dotnet msbuild -getItem` for (msbuildEvaluator.ts).
    private val itemTypes = setOf("compile", "none", "content", "reqnrollfeaturefiles")

    private const val SHARED_ITEMS_EXTENSION = ".projitems"

    /** Full paths of the files [projectFilePath] compiles from the shared projects it imports, deduplicated, in declaration order. */
    fun getImportedFiles(projectFilePath: String): List<String> =
        getSharedItemsImports(projectFilePath)
            .flatMap { getItemFiles(it) }
            .distinctBy { it.lowercase() }

    /** Full paths of the existing `.projitems` files [projectFilePath] imports. */
    fun getSharedItemsImports(projectFilePath: String): List<String> {
        val root = tryLoad(projectFilePath) ?: return emptyList()
        val projectFile = File(projectFilePath).absoluteFile
        val projectDirectory = projectFile.parent ?: return emptyList()

        return root.descendants()
            .filter { it.itemName() == "Import" }
            .mapNotNull { element -> element.getAttribute("Project").takeIf { it.isNotBlank() } }
            .mapNotNull { resolvePath(it, projectDirectory, projectFile.path) }
            .filter { it.endsWith(SHARED_ITEMS_EXTENSION, ignoreCase = true) && File(it).isFile }
            .distinctBy { it.lowercase() }
            .toList()
    }

    /** Full paths of the existing files the source-carrying items of [projItemsPath] name, after its `Exclude`s and `Remove`s. */
    fun getItemFiles(projItemsPath: String): List<String> {
        val root = tryLoad(projItemsPath) ?: return emptyList()
        val thisFile = File(projItemsPath).absoluteFile
        val directory = thisFile.parent ?: return emptyList()

        val files = mutableListOf<String>()
        root.descendants()
            .filter { it.itemName().lowercase() in itemTypes }
            // Only items inside an ItemGroup; a property named e.g. "Content" is not an item.
            .filter { (it.parentNode as? Element)?.itemName() == "ItemGroup" }
            .forEach { item ->
                val include = item.getAttribute("Include")
                val remove = item.getAttribute("Remove")
                if (include.isNotBlank()) {
                    val excluded = expand(item.getAttribute("Exclude"), directory, thisFile.path).map { it.lowercase() }.toSet()
                    files += expand(include, directory, thisFile.path).filter { it.lowercase() !in excluded }
                } else if (remove.isNotBlank()) {
                    val removed = expand(remove, directory, thisFile.path).map { it.lowercase() }.toSet()
                    files.removeAll { it.lowercase() in removed }
                }
            }
        return files.distinctBy { it.lowercase() }
    }

    private fun expand(itemSpec: String, directory: String, thisFilePath: String): List<String> =
        itemSpec.split(';')
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .mapNotNull { resolvePath(it, directory, thisFilePath) }
            .flatMap { path ->
                if (path.none { it == '*' || it == '?' }) {
                    if (File(path).isFile) listOf(path) else emptyList()
                } else {
                    expandWildcard(path)
                }
            }

    // Walks the non-wildcard prefix of `pattern` and keeps the files whose path relative to that
    // prefix matches the rest of it.
    private fun expandWildcard(pattern: String): List<String> {
        val segments = pattern.split(File.separatorChar)
        val firstWildcard = segments.indexOfFirst { s -> s.any { it == '*' || it == '?' } }
        val baseDirectory = segments.take(firstWildcard).joinToString(File.separator)
        val base = File(baseDirectory)
        if (baseDirectory.isEmpty() || !base.isDirectory) return emptyList()

        val matcher = Regex("^" + globToRegex(segments.drop(firstWildcard).joinToString(File.separator)) + "$", RegexOption.IGNORE_CASE)
        return try {
            base.walkTopDown()
                .filter { it.isFile }
                .map { it.path }
                .filter { matcher.matches(it.substring(baseDirectory.length).trimStart(File.separatorChar)) }
                .toList()
        } catch (_: IOException) {
            emptyList()
        } catch (_: SecurityException) {
            emptyList()
        }
    }

    private fun globToRegex(glob: String): String {
        val separator = """[\\/]"""
        val notSeparator = """[^\\/]"""
        val regex = StringBuilder()
        var i = 0
        while (i < glob.length) {
            val c = glob[i]
            when {
                c == '*' && i + 1 < glob.length && glob[i + 1] == '*' -> {
                    // `**/` matches zero or more whole directories; a trailing `**` matches anything.
                    val followedBySeparator = i + 2 < glob.length && glob[i + 2] == File.separatorChar
                    regex.append(if (followedBySeparator) "(?:.*$separator)?" else ".*")
                    i += if (followedBySeparator) 2 else 1
                }
                c == '*' -> regex.append("$notSeparator*")
                c == '?' -> regex.append(notSeparator)
                c == File.separatorChar -> regex.append(separator)
                else -> regex.append(Regex.escape(c.toString()))
            }
            i++
        }
        return regex.toString()
    }

    // Substitutes the properties Visual Studio writes into imports and .projitems, normalizes the
    // separators (a .projitems written on Windows uses `\` even when Rider runs on Linux), and
    // roots a relative path at `directory`. Null for a path that still names another property.
    private fun resolvePath(path: String, directory: String, thisFilePath: String): String? {
        val withSeparator = if (directory.endsWith(File.separator)) directory else directory + File.separator
        val substituted = path
            .replace("\$(MSBuildThisFileDirectory)", withSeparator, ignoreCase = true)
            .replace("\$(MSBuildProjectDirectory)", directory, ignoreCase = true)
            .replace("\$(MSBuildThisFileFullPath)", thisFilePath, ignoreCase = true)
        if (substituted.contains("\$(")) return null

        val normalized = substituted.replace('\\', File.separatorChar).replace('/', File.separatorChar)
        val file = File(normalized)
        val rooted = (if (file.isAbsolute) file else File(directory, normalized)).path
        // Collapse `.`/`..` on the wildcard-free prefix only: on Windows, toPath() rejects `*`/`?`.
        val wildcard = rooted.indexOfFirst { it == '*' || it == '?' }
        return try {
            if (wildcard < 0) return File(rooted).toPath().normalize().toString()
            val prefixEnd = rooted.lastIndexOf(File.separatorChar, wildcard)
            if (prefixEnd < 0) return null
            File(rooted.substring(0, prefixEnd)).toPath().normalize().toString() + rooted.substring(prefixEnd)
        } catch (_: java.nio.file.InvalidPathException) {
            null
        }
    }

    private fun tryLoad(path: String): Element? {
        val file = File(path)
        if (!file.isFile) return null
        return try {
            val factory = DocumentBuilderFactory.newInstance().apply {
                isNamespaceAware = true
                setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true)
                setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
            }
            factory.newDocumentBuilder().parse(file).documentElement
        } catch (_: Exception) {
            // Malformed XML, unreadable file, or a parser that rejects a feature: contribute nothing.
            null
        }
    }

    private fun Element.itemName(): String = localName ?: tagName

    private fun Element.descendants(): Sequence<Element> {
        val nodes = getElementsByTagName("*")
        return (0 until nodes.length).asSequence().map { nodes.item(it) as Element }
    }
}
