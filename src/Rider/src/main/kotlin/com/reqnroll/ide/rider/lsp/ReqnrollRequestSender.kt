package com.reqnroll.ide.rider.lsp

import com.intellij.openapi.progress.ProcessCanceledException
import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerManager
import com.reqnroll.ide.rider.logging.ReqnrollDebugLogger
import org.eclipse.lsp4j.jsonrpc.ResponseErrorException
import com.reqnroll.ide.rider.lsp.protocol.FindStepUsagesResponse
import com.reqnroll.ide.rider.lsp.protocol.FindUnusedStepDefinitionsResponse
import com.reqnroll.ide.rider.lsp.protocol.GoToHooksRequestParams
import com.reqnroll.ide.rider.lsp.protocol.GoToHooksResponse
import com.reqnroll.ide.rider.lsp.protocol.GoToMatchingScenariosResponse
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollEmptyParams
import com.reqnroll.ide.rider.lsp.protocol.ReqnrollLanguageServer
import com.reqnroll.ide.rider.lsp.protocol.RenameTargetsResponse
import com.reqnroll.ide.rider.lsp.protocol.ResolveTestTargetsParams
import com.reqnroll.ide.rider.lsp.protocol.ResolveTestTargetsResponse
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.CodeLensParams
import org.eclipse.lsp4j.DocumentOnTypeFormattingParams
import org.eclipse.lsp4j.DocumentSymbol
import org.eclipse.lsp4j.DocumentSymbolParams
import org.eclipse.lsp4j.ExecuteCommandParams
import org.eclipse.lsp4j.FoldingRange
import org.eclipse.lsp4j.FoldingRangeRequestParams
import org.eclipse.lsp4j.FormattingOptions
import org.eclipse.lsp4j.InlayHint
import org.eclipse.lsp4j.InlayHintParams
import org.eclipse.lsp4j.ReferenceContext
import org.eclipse.lsp4j.ReferenceParams
import org.eclipse.lsp4j.RenameParams
import org.eclipse.lsp4j.TextDocumentIdentifier
import org.eclipse.lsp4j.TextDocumentPositionParams
import org.eclipse.lsp4j.TextEdit
import org.eclipse.lsp4j.WorkspaceEdit
import org.eclipse.lsp4j.Position as Lsp4jPosition
import org.eclipse.lsp4j.Range as Lsp4jRange

/**
 * Sends the reqnroll-prefixed client-to-server *requests* (as opposed to
 * [ReqnrollNotificationSender]'s fire-and-forget notifications) via `LspServer.sendRequestSync`,
 * confirmed to exist on Rider 2024.3.5's actual `LspServer` interface by decompiling
 * `com.intellij.platform.lsp.api.LspServer`. `sendRequestSync` blocks the calling thread until a
 * response arrives or the timeout elapses — callers must invoke this from a background thread
 * (e.g. inside a `Task.Backgroundable`), never directly from `AnAction.actionPerformed`'s EDT
 * dispatch.
 */
object ReqnrollRequestSender {
    private const val FIND_UNUSED_TIMEOUT_MS = 30_000
    private const val FIND_USAGES_TIMEOUT_MS = 10_000
    private const val CODE_LENS_TIMEOUT_MS = 10_000
    private const val INLAY_HINT_TIMEOUT_MS = 10_000
    private const val ON_TYPE_FORMATTING_TIMEOUT_MS = 10_000
    private const val FOLDING_RANGE_TIMEOUT_MS = 10_000
    private const val GO_TO_HOOKS_TIMEOUT_MS = 10_000
    private const val GO_TO_MATCHING_SCENARIOS_TIMEOUT_MS = 10_000
    private const val TOGGLE_COMMENT_TIMEOUT_MS = 10_000
    private const val RENAME_TARGETS_TIMEOUT_MS = 10_000
    private const val RENAME_TIMEOUT_MS = 10_000
    private const val DOCUMENT_SYMBOL_TIMEOUT_MS = 10_000
    private const val RESOLVE_TEST_TARGETS_TIMEOUT_MS = 10_000

    /** Runs `reqnroll/findUnusedStepDefinitions`. Returns null if no Reqnroll LSP server is running, or on failure. */
    fun findUnusedStepDefinitions(project: Project): FindUnusedStepDefinitionsResponse? {
        val server = firstRunningServer(project) ?: return null
        return try {
            server.sendRequestSync(FIND_UNUSED_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).findUnusedStepDefinitions(ReqnrollEmptyParams())
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("findUnusedStepDefinitions: request failed", ex, curated = true)
            null
        }
    }

    /** Runs `reqnroll/findStepUsages` for the binding at (uri, line, character). Returns null if no Reqnroll LSP server is running, or on failure. */
    fun findStepUsages(project: Project, uri: String, line: Int, character: Int): FindStepUsagesResponse? {
        val server = firstRunningServer(project) ?: return null
        val params = ReferenceParams().apply {
            textDocument = TextDocumentIdentifier(uri)
            position = Lsp4jPosition(line, character)
            context = ReferenceContext(false)
        }
        return try {
            server.sendRequestSync(FIND_USAGES_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).findStepUsages(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("findStepUsages: request failed", ex, curated = true)
            null
        }
    }

    /**
     * Runs the *standard* `textDocument/codeLens` request (step-usage-count lenses for `.cs`
     * files — see StepCodeLensHandler.cs). Standard LSP methods are already declared on LSP4J's
     * base `LanguageServer`/`TextDocumentService` interfaces, so no custom `@JsonRequest` method
     * or cast to `ReqnrollLanguageServer` is needed, unlike the custom reqnroll-prefixed methods above.
     */
    fun codeLens(project: Project, uri: String): List<CodeLens>? {
        val server = firstRunningServer(project) ?: return null
        val params = CodeLensParams(TextDocumentIdentifier(uri))
        return try {
            server.sendRequestSync(CODE_LENS_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.codeLens(params)
            }?.filterNotNull()
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("codeLens: request failed", ex)
            null
        }
    }

    /** Runs the *standard* `textDocument/inlayHint` request (binding-info hints for `.feature` files — see FeatureInlayHintHandler.cs) covering the given line range. */
    fun inlayHint(project: Project, uri: String, startLine: Int, endLine: Int): List<InlayHint>? {
        val server = firstRunningServer(project) ?: return null
        val range = Lsp4jRange(Lsp4jPosition(startLine, 0), Lsp4jPosition(endLine, Int.MAX_VALUE))
        val params = InlayHintParams(TextDocumentIdentifier(uri), range)
        return try {
            server.sendRequestSync(INLAY_HINT_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.inlayHint(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("inlayHint: request failed", ex)
            null
        }
    }

    /** Runs the *standard* `textDocument/onTypeFormatting` request (table-column realignment for `.feature` files — see GherkinFormattingHandler.cs) at (line, character), triggered by typing [triggerChar]. */
    fun onTypeFormatting(
        project: Project,
        uri: String,
        line: Int,
        character: Int,
        triggerChar: String,
        tabSize: Int,
        insertSpaces: Boolean,
    ): List<TextEdit>? {
        val server = firstRunningServer(project) ?: return null
        val params = DocumentOnTypeFormattingParams(
            TextDocumentIdentifier(uri),
            FormattingOptions(tabSize, insertSpaces),
            Lsp4jPosition(line, character),
            triggerChar,
        )
        return try {
            server.sendRequestSync(ON_TYPE_FORMATTING_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.onTypeFormatting(params)
            }?.filterNotNull()
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("onTypeFormatting: request failed", ex)
            null
        }
    }

    /**
     * Runs the *standard* `textDocument/foldingRange` request (Code Folding for `.feature`
     * files — see FeatureFoldingRangeHandler.cs). Standard LSP method, so — like [codeLens] and
     * [inlayHint] — no custom `@JsonRequest` method or cast to `ReqnrollLanguageServer` is needed.
     */
    fun foldingRange(project: Project, uri: String): List<FoldingRange>? {
        val server = firstRunningServer(project) ?: return null
        val params = FoldingRangeRequestParams(TextDocumentIdentifier(uri))
        return try {
            server.sendRequestSync(FOLDING_RANGE_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.foldingRange(params)
            }?.filterNotNull()
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("foldingRange: request failed", ex)
            null
        }
    }

    /**
     * Runs `reqnroll/goToHooks` for the position (uri, line, character) in a `.feature` file.
     * [ownLevelOnly] is forwarded from the hook-count CodeVision lens's `command.arguments`
     * (see HookCodeVisionProvider) so the response matches exactly what the lens counted; manual
     * invocations (GoToHooksAction) leave it at the default `false` (cumulative). Returns null if
     * no Reqnroll LSP server is running, or on failure.
     */
    fun goToHooks(
        project: Project, uri: String, line: Int, character: Int, ownLevelOnly: Boolean = false,
    ): GoToHooksResponse? {
        val server = firstRunningServer(project) ?: return null
        val params = GoToHooksRequestParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character), ownLevelOnly)
        return try {
            server.sendRequestSync(GO_TO_HOOKS_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).goToHooks(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("goToHooks: request failed", ex, curated = true)
            null
        }
    }

    /** Runs `reqnroll/goToMatchingScenarios` for the hook-binding attribute at (uri, line, character) in a `.cs` file (issue #373). Returns null if no Reqnroll LSP server is running, or on failure. */
    fun goToMatchingScenarios(project: Project, uri: String, line: Int, character: Int): GoToMatchingScenariosResponse? {
        val server = firstRunningServer(project) ?: return null
        val params = TextDocumentPositionParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character))
        return try {
            server.sendRequestSync(GO_TO_MATCHING_SCENARIOS_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).goToMatchingScenarios(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("goToMatchingScenarios: request failed", ex, curated = true)
            null
        }
    }

    /**
     * Runs the *standard* `workspace/executeCommand` request for `reqnroll.toggleComment`
     * (Comment/Uncomment toggle — see CommentToggleHandler.cs). There is no dedicated,
     * reqnroll-prefixed custom method for this feature, unlike findStepUsages/goToHooks — the server responds
     * by sending a `workspace/applyEdit` *request back to the client*, which Rider's platform
     * `Lsp4jClient.applyEdit` already applies natively (confirmed by decompiling — it's a `final`
     * method on the base class, not something [ReqnrollLspServerDescriptor.createLsp4jClient]'s
     * wrapping needs to add a consumer for), so this call's own return value is unused; callers
     * only care whether the command was successfully dispatched.
     */
    fun toggleComment(project: Project, uri: String, startLine: Int, endLine: Int): Boolean {
        val server = firstRunningServer(project) ?: return false
        val params = ExecuteCommandParams("reqnroll.toggleComment", listOf(uri, startLine, endLine))
        return try {
            server.sendRequestSync(TOGGLE_COMMENT_TIMEOUT_MS) { languageServer ->
                languageServer.workspaceService.executeCommand(params)
            }
            true
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("toggleComment: request failed", ex, curated = true)
            false
        }
    }

    /** Runs `reqnroll/renameTargets` for the position (uri, line, character). Returns null if no Reqnroll LSP server is running, or on failure. */
    fun renameTargets(project: Project, uri: String, line: Int, character: Int): RenameTargetsResponse? {
        val server = firstRunningServer(project) ?: return null
        val params = TextDocumentPositionParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character))
        return try {
            server.sendRequestSync(RENAME_TARGETS_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).renameTargets(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("renameTargets: request failed", ex, curated = true)
            null
        }
    }

    /**
     * Runs the *standard* `textDocument/rename` request with [newName] at (uri, line, character).
     * Standard LSP method, so — like [codeLens]/[foldingRange] — no custom `@JsonRequest` method
     * or cast to `ReqnrollLanguageServer` is needed. Rider has no native rename bridge (confirmed
     * by decompiling `LspServerDescriptor` — no `lspRenameSupport`-style customization exists),
     * so callers must apply the returned `WorkspaceEdit` themselves; see `RenameWorkspaceEditApplier`.
     *
     * Returns [RenameOutcome.Failed] with the server's own human-readable reason (issue #650) when
     * the server rejects the rename — e.g. a step-rename validation failure — rather than the
     * blanket "request failed" `null` every other method in this file returns on any exception.
     * A rejected rename follows a modal "pick a target, type a new expression" flow the user just
     * completed, so [RenameStepRunner] showing no explanation at all (like a routine navigation
     * miss would) is worse here than for the rest of this file's silently-null methods.
     */
    fun rename(project: Project, uri: String, line: Int, character: Int, newName: String): RenameOutcome {
        val server = firstRunningServer(project)
            ?: return RenameOutcome.Failed("The Reqnroll LSP server is not running or did not respond.")
        val params = RenameParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character), newName)
        return try {
            val edit = server.sendRequestSync(RENAME_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.rename(params)
            }
            if (edit != null) {
                RenameOutcome.Success(edit)
            } else {
                RenameOutcome.Failed("Rename failed — the new expression may be invalid, or nothing to rename.")
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("rename: request failed", ex, curated = true)
            val message = extractResponseErrorMessage(ex)
                ?: "Rename failed — the new expression may be invalid, or nothing to rename."
            RenameOutcome.Failed(message)
        }
    }

    /**
     * Unwraps the server's human-readable reason (issue #650) from a failed `textDocument/rename`
     * request, when the failure was a real JSON-RPC error response rather than some other
     * exception (timeout, I/O error, cancellation). Checks the exception itself and one level of
     * [Throwable.cause], since `LspServer.sendRequestSync` may surface a [ResponseErrorException]
     * directly or wrapped (e.g. in an `ExecutionException` from the underlying
     * `CompletableFuture.get`) — Rider's `LspServer` interface gives no documented guarantee
     * either way. Returns null for any other kind of failure, so callers fall back to a generic
     * message instead of showing something misleading.
     */
    internal fun extractResponseErrorMessage(ex: Throwable): String? =
        ((ex as? ResponseErrorException) ?: (ex.cause as? ResponseErrorException))
            ?.responseError?.message

    /**
     * Runs the *standard* `textDocument/documentSymbol` request (Feature/Rule/Scenario/Step)
     * outline for `.feature` files — see FeatureDocumentSymbolHandler.cs). Standard LSP method,
     * so — like [codeLens]/[foldingRange] — no custom `@JsonRequest` method or cast to
     * `ReqnrollLanguageServer` is needed. The server always sends the nested `DocumentSymbol`
     * shape (not flat `SymbolInformation`) to Rider, since Rider's generic LSP client declares
     * `hierarchicalDocumentSymbolSupport` by platform default (matching VS Code) — so only the
     * `Either.right` (`DocumentSymbol`) side is ever populated in practice; entries that somehow
     * come back as `SymbolInformation` are dropped rather than crashing.
     */
    fun documentSymbol(project: Project, uri: String): List<DocumentSymbol>? {
        val server = firstRunningServer(project) ?: return null
        val params = DocumentSymbolParams(TextDocumentIdentifier(uri))
        return try {
            server.sendRequestSync(DOCUMENT_SYMBOL_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.documentSymbol(params)
            }?.mapNotNull { it.right }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("documentSymbol: request failed", ex)
            null
        }
    }

    /**
     * Runs `reqnroll/resolveTestTargets` for the range [(startLine, startChar), (endLine, endChar)]
     * in a `.feature` file (design doc §3/§4, issue #262) — resolves the generated test method(s)
     * the scenario/Outline/example row at that range corresponds to. Returns null if no Reqnroll
     * LSP server is running, or on failure.
     */
    fun resolveTestTargets(
        project: Project, uri: String, startLine: Int, startChar: Int, endLine: Int, endChar: Int,
    ): ResolveTestTargetsResponse? {
        val server = firstRunningServer(project) ?: return null
        val params = ResolveTestTargetsParams(
            TextDocumentIdentifier(uri),
            Lsp4jRange(Lsp4jPosition(startLine, startChar), Lsp4jPosition(endLine, endChar)),
        )
        return try {
            server.sendRequestSync(RESOLVE_TEST_TARGETS_TIMEOUT_MS) { languageServer ->
                (languageServer as ReqnrollLanguageServer).resolveTestTargets(params)
            }
        } catch (ex: ProcessCanceledException) {
            throw ex
        } catch (ex: Exception) {
            ReqnrollDebugLogger.warn("resolveTestTargets: request failed", ex, curated = true)
            null
        }
    }

    private fun firstRunningServer(project: Project) =
        LspServerManager.getInstance(project)
            .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)
            .firstOrNull()
            .also {
                if (it == null)
                    ReqnrollDebugLogger.warn("ReqnrollRequestSender: no Reqnroll LSP server running")
            }
}

/**
 * Outcome of [ReqnrollRequestSender.rename]: either a [WorkspaceEdit] to apply, or a reason it
 * couldn't be produced. Distinguishes "the server rejected this rename for an explainable reason"
 * (issue #650) from every other request in this file's blanket "something went wrong" `null`, so
 * [com.reqnroll.ide.rider.actions.RenameStepRunner] can show the server's actual message.
 */
sealed interface RenameOutcome {
    data class Success(val edit: WorkspaceEdit) : RenameOutcome
    data class Failed(val message: String) : RenameOutcome
}
