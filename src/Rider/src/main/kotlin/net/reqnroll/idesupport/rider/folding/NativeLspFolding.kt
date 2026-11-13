package net.reqnroll.idesupport.rider.folding

/**
 * Whether the running Rider folds from the server's `textDocument/foldingRange` response by itself.
 *
 * Rider's generic LSP client does so from 2025.2.2, through a `FoldingBuilder` registered for every language,
 * unless a descriptor turns it off. The public customization class that arrived with it,
 * `LspFoldingRangeSupport`, is what is looked for: its presence means the feature is there. It cannot be named in
 * code because the plugin compiles against an older Rider that does not have it, and the platform's own implementation
 * classes are not visible to the plugin's class loader.
 */
internal object NativeLspFolding {
    private const val SUPPORT_CLASS = "com.intellij.platform.lsp.api.customization.LspFoldingRangeSupport"

    val isAvailable: Boolean by lazy { classExists(SUPPORT_CLASS, NativeLspFolding::class.java.classLoader) }

    /** True when [name] can be loaded by [loader]; the class is not initialised. */
    internal fun classExists(name: String, loader: ClassLoader?): Boolean =
        try {
            Class.forName(name, false, loader)
            true
        } catch (_: ClassNotFoundException) {
            false
        } catch (_: LinkageError) {
            false
        }
}
