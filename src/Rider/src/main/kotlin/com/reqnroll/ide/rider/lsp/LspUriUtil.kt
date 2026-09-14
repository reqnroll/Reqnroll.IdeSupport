package com.reqnroll.ide.rider.lsp

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
