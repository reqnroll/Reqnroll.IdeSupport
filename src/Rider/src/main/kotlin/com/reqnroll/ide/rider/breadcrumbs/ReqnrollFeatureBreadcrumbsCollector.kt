package com.reqnroll.ide.rider.breadcrumbs

import com.intellij.codeInsight.breadcrumbs.FileBreadcrumbsCollector
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.ScrollType
import com.intellij.openapi.editor.event.CaretEvent
import com.intellij.openapi.editor.event.CaretListener
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.ui.components.breadcrumbs.Crumb
import com.intellij.util.Alarm
import com.intellij.util.io.URLUtil
import com.intellij.xml.breadcrumbs.NavigatableCrumb
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import com.reqnroll.ide.rider.lsp.ReqnrollRequestSender
import com.reqnroll.ide.rider.structureview.ReqnrollSymbolTreeElement
import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.Position
import java.util.concurrent.ConcurrentHashMap
import javax.swing.Icon

/**
 * Feeds the editor breadcrumb bar for `.feature` files from `textDocument/documentSymbol` —
 * issue #161. `com.intellij.ui.breadcrumbs.BreadcrumbsProvider` (the standard, `Language`-keyed
 * EP) never fires here for the same reason every other PSI-scoped EP in this plugin doesn't:
 * `.feature` files have no `ParserDefinition` (see `ReqnrollFeatureInlayHintsController`'s doc
 * comment). `FileBreadcrumbsCollector` (`com.intellij.fileBreadcrumbsCollector`) is a
 * `VirtualFile`/`Document`/offset-scoped EP instead — no PSI required — matching the same "operate
 * directly on the document" pattern already used for folding, inlay hints, and the Structure View
 * tool window (issue #163).
 *
 * [requiresProvider] must return `false`: confirmed via decompiling
 * `BreadcrumbsInitializingActivityKt.isSuitable` that a collector whose [requiresProvider] is
 * `true` (the default) gets skipped entirely for files with no matching `BreadcrumbsProvider` —
 * which is every `.feature` file, since none exists for `ReqnrollFeatureLanguage`.
 *
 * One instance handles every `.feature` file in the project (the platform keeps a single instance
 * per registered `FileBreadcrumbsCollector`), so [symbolsByUri] is a natural, bounded (one entry
 * per distinct `.feature` file ever opened) cache: [computeCrumbs] must return synchronously, so
 * it reads from this cache rather than calling the LSP server inline.
 *
 * [watchForChanges] is **not** a one-time "editor just opened" hook, despite its placement —
 * confirmed live (issue #161 follow-up) and by decompiling `BreadcrumbsXmlWrapper.computeCrumbs`:
 * it calls `findCollectorFor(...)`, which calls `watchForChanges` again, on *every* breadcrumb
 * recomputation. Unconditionally kicking off an LSP fetch there — as the first version of this
 * class did — created a runaway loop: fetch completes → `changeCallback.run()`
 * (`BreadcrumbsPanel.queueUpdate()`) → another `computeCrumbs` → `watchForChanges` again → another
 * fetch, forever, hitting the LSP server every ~200ms. [watchedDisposables] makes setup
 * idempotent per `disposable` (a fresh instance per editor-open, so re-opening a previously closed
 * `.feature` tab still re-initializes correctly): the debounced document-edit refresh (mirroring
 * [com.reqnroll.ide.rider.folding.ReqnrollFeatureFoldingController]'s `Alarm`/`DocumentListener`
 * pattern) and the initial fetch happen once per `disposable`, not once per recomputation. Caret
 * movement alone never re-hits the LSP server either: it just re-invokes [computeCrumbs] against
 * the already-cached symbol tree to recompute which Feature/Rule/Scenario/Step chain contains the
 * new offset.
 *
 * [refreshOpenFeatureEditors] exists for the same reason
 * [com.reqnroll.ide.rider.folding.ReqnrollFeatureFoldingController.refreshOpenFeatureEditors]
 * does: confirmed live (issue #161 follow-up) that a `.feature` editor open before the LSP server
 * is up gets a permanently `null` first fetch — [watchForChanges]'s per-disposable idempotency
 * guard means nothing else naturally retries it for a tab the user doesn't revisit or edit. Wired
 * into [com.reqnroll.ide.rider.lsp.ReqnrollInlayHintRefreshInterceptor] alongside the other
 * once-the-server-is-actually-up refreshes.
 */
class ReqnrollFeatureBreadcrumbsCollector(private val project: Project) : FileBreadcrumbsCollector() {
    private val symbolsByUri = ConcurrentHashMap<String, List<DocumentSymbol>>()
    private val watchedDisposables = ConcurrentHashMap.newKeySet<Disposable>()
    private val changeCallbacksByUri = ConcurrentHashMap<String, Runnable>()

    init {
        instances[project] = this
        Disposer.register(project) { instances.remove(project, this) }
    }

    override fun handlesFile(file: VirtualFile): Boolean = file.extension.equals("feature", ignoreCase = true)

    override fun requiresProvider(): Boolean = false

    override fun watchForChanges(file: VirtualFile, editor: Editor, disposable: Disposable, changeCallback: Runnable) {
        if (!watchedDisposables.add(disposable)) return
        val uri = uriOf(file)
        changeCallbacksByUri[uri] = changeCallback
        Disposer.register(disposable) {
            watchedDisposables.remove(disposable)
            changeCallbacksByUri.remove(uri, changeCallback)
        }

        val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, disposable)

        editor.document.addDocumentListener(
            object : DocumentListener {
                override fun documentChanged(event: DocumentEvent) {
                    alarm.cancelAllRequests()
                    if (!alarm.isDisposed) alarm.addRequest({ refreshSymbols(uri, changeCallback) }, DEBOUNCE_MS)
                }
            },
            disposable,
        )
        editor.caretModel.addCaretListener(
            object : CaretListener {
                override fun caretPositionChanged(event: CaretEvent) = changeCallback.run()
            },
            disposable,
        )

        refreshSymbols(uri, changeCallback)
    }

    override fun computeCrumbs(file: VirtualFile, document: Document, offset: Int, forcedShown: Boolean?): Iterable<Crumb> {
        val symbols = symbolsByUri[uriOf(file)] ?: return emptyList()
        return buildCrumbs(document, symbols, offset)
    }

    private fun refreshSymbols(uri: String, changeCallback: Runnable) {
        if (project.isDisposed) return

        ApplicationManager.getApplication().executeOnPooledThread {
            if (project.isDisposed) return@executeOnPooledThread

            val result = ReqnrollRequestSender.documentSymbol(project, uri)
            ReqnrollDebugLogger.info("ReqnrollFeatureBreadcrumbsCollector: ${result?.size ?: "null"} top-level symbol(s) for $uri")
            if (result == null) return@executeOnPooledThread

            symbolsByUri[uri] = result
            ApplicationManager.getApplication().invokeLater(
                { if (!project.isDisposed) changeCallback.run() },
                ModalityState.any(),
            )
        }
    }

    private fun uriOf(file: VirtualFile): String = VirtualFileManager.constructUrl("file", URLUtil.encodePath(file.path))

    companion object {
        private const val DEBOUNCE_MS = 400
        private val instances = ConcurrentHashMap<Project, ReqnrollFeatureBreadcrumbsCollector>()

        /** Re-fetches every currently-watched `.feature` file's breadcrumb symbols for [project]. No-op if no collector is registered (e.g. no `.feature` file has been opened yet). */
        fun refreshOpenFeatureEditors(project: Project) {
            val collector = instances[project] ?: return
            for ((uri, changeCallback) in collector.changeCallbacksByUri) {
                collector.refreshSymbols(uri, changeCallback)
            }
        }

        /** Walks from [symbols] (top-level) down through whichever child's range contains [offset] at each level. */
        private fun buildCrumbs(document: Document, symbols: List<DocumentSymbol>, offset: Int): List<Crumb> =
            selectCrumbTrail(symbols, offset) { document.offsetOf(it) }.map { ReqnrollBreadcrumb(document, it) }
    }
}

/**
 * Walks from [symbols] (top-level) down through whichever child's range contains [offset] at each
 * level, until no child matches -- e.g. Feature -> Scenario -> Step. [offsetOf] converts an LSP
 * `Position` to a document offset; injected (rather than requiring a live `Document`) so this
 * descent logic is unit-testable without a `Document` fixture -- production code passes
 * `Document.offsetOf` (see below). `internal` for the same reason.
 */
internal fun selectCrumbTrail(
    symbols: List<DocumentSymbol>,
    offset: Int,
    offsetOf: (Position) -> Int,
): List<DocumentSymbol> {
    val trail = mutableListOf<DocumentSymbol>()
    var level = symbols
    while (true) {
        val match = level.firstOrNull { offset in offsetOf(it.range.start)..offsetOf(it.range.end) } ?: break
        trail += match
        level = match.children.orEmpty()
    }
    return trail
}

/** Converts a 0-based LSP [Position] to a document offset, clamping defensively to the document's actual bounds. */
private fun Document.offsetOf(position: Position): Int {
    val line = position.line.coerceIn(0, (lineCount - 1).coerceAtLeast(0))
    val lineStart = getLineStartOffset(line)
    val lineEnd = getLineEndOffset(line)
    return (lineStart + position.character).coerceIn(lineStart, lineEnd)
}

/** One Feature/Rule/Background/Scenario/Step/Examples segment of the breadcrumb trail. */
private class ReqnrollBreadcrumb(
    document: Document,
    private val symbol: DocumentSymbol,
) : NavigatableCrumb {
    private val highlightRange = TextRange(document.offsetOf(symbol.range.start), document.offsetOf(symbol.range.end))
    private val anchorOffset = document.offsetOf(symbol.selectionRange.start)

    override fun getIcon(): Icon = ReqnrollSymbolTreeElement.iconFor(symbol.kind)

    override fun getText(): String = symbol.name

    override fun getTooltip(): String? = symbol.detail

    override fun getHighlightRange(): TextRange = highlightRange

    override fun getAnchorOffset(): Int = anchorOffset

    override fun navigate(editor: Editor, requestFocus: Boolean) {
        editor.caretModel.moveToOffset(anchorOffset)
        editor.scrollingModel.scrollToCaret(ScrollType.CENTER)
    }
}
