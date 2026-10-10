package net.reqnroll.idesupport.rider.lsp

import com.intellij.util.io.URLUtil
import java.net.URI
import java.nio.file.Paths

/**
 * Converts an LSP `DocumentUri` (percent-encoded per the LSP spec, e.g.
 * `file:///repo/Price%20-%20Copy.feature`) to a local filesystem path suitable for
 * `LocalFileSystem`/`VirtualFileManager` lookups.
 *
 * IntelliJ's VFS URLs are *not* percent-encoded (a file named `Price - Copy.feature` has a
 * literal space in its VFS url), unlike LSP `DocumentUri`s. Handing a raw LSP URI straight to
 * `VirtualFileManager.findFileByUrl` therefore silently fails to resolve any path containing a
 * character that needed percent-encoding — it only "worked" for paths with no such characters.
 * Decoding through `java.net.URI`/`Paths.get` first (the same as the `.NET Uri.LocalPath` /
 * `vscode.Uri.fsPath` behavior the VS and VS Code clients already get for free) avoids that.
 *
 * Returns `null` if [uri] isn't a valid absolute `file:` URI.
 */
fun lspUriToLocalPath(uri: String): String? =
    try {
        Paths.get(URI(uri)).toString().replace('\\', '/')
    } catch (e: Exception) {
        null
    }

/**
 * The inverse of [lspUriToLocalPath]: the LSP `DocumentUri` for a local file path, in exactly the
 * form Rider's own LSP client uses for `textDocument/didOpen` (`LspServerDescriptor.getFileUri`,
 * confirmed by decompiling the 2024.3.5 jar): percent-encoded path, `file:///` plus the path for
 * a Windows drive path, with the drive letter lower-cased.
 *
 * The server keys its document buffers, match sets and binding registries by this string, so every
 * request the plugin sends itself must use the same form as the platform's didOpen or it silently
 * finds nothing (#909). Do not build it with `VirtualFileManager.constructUrl("file", path)`: that
 * is just `"file://" + path`, which for `W:/repo/F.feature` yields the malformed
 * `file://W:/repo/F.feature` (two slashes, upper-case drive) — correct only for paths starting
 * with `/`.
 */
fun localPathToLspUri(path: String): String {
    val encoded = URLUtil.encodePath(path.replace('\\', '/'))
    return when {
        WINDOWS_DRIVE_PATH.containsMatchIn(encoded) ->
            "file:///" + encoded[0].lowercaseChar() + encoded.substring(1)
        encoded.startsWith("//") -> "file:$encoded" // UNC: file://server/share/...
        encoded.startsWith("/") -> "file://$encoded"
        else -> "file:///$encoded"
    }
}

private val WINDOWS_DRIVE_PATH = Regex("^[A-Za-z]:(/|$)")
