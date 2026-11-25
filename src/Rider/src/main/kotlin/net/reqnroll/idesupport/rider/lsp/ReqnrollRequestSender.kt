package net.reqnroll.idesupport.rider.lsp

import com.intellij.openapi.progress.ProcessCanceledException
import com.intellij.openapi.progress.runBlockingMaybeCancellable
import com.intellij.openapi.project.Project
import com.intellij.platform.lsp.api.LspServerManager
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import kotlinx.coroutines.withTimeoutOrNull
import net.reqnroll.idesupport.rider.lsp.protocol.FindStepUsagesResponse
import net.reqnroll.idesupport.rider.lsp.protocol.FindUnusedStepDefinitionsResponse
import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeParams
import net.reqnroll.idesupport.rider.lsp.protocol.GetTestOutcomeResponse
import net.reqnroll.idesupport.rider.lsp.protocol.FindHooksRequestParams
import net.reqnroll.idesupport.rider.lsp.protocol.FindHooksResponse
import net.reqnroll.idesupport.rider.lsp.protocol.FindMatchingScenariosResponse
import net.reqnroll.idesupport.rider.lsp.protocol.RegisterTestRunResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ReqnrollEmptyParams
import net.reqnroll.idesupport.rider.lsp.protocol.ReqnrollLanguageServer
import net.reqnroll.idesupport.rider.lsp.protocol.RenameTargetsResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ResolveContainerTestTargetsParams
import net.reqnroll.idesupport.rider.lsp.protocol.ResolveContainerTestTargetsResponse
import net.reqnroll.idesupport.rider.lsp.protocol.ResolveTestTargetsParams
import net.reqnroll.idesupport.rider.lsp.protocol.ResolveTestTargetsResponse
import org.eclipse.lsp4j.CodeLens
import org.eclipse.lsp4j.CodeLensParams
import org.eclipse.lsp4j.DefinitionParams
import org.eclipse.lsp4j.Location
import org.eclipse.lsp4j.DocumentLink
import org.eclipse.lsp4j.DocumentLinkParams
import org.eclipse.lsp4j.DocumentFormattingParams
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
import org.eclipse.lsp4j.services.LanguageServer
import java.util.concurrent.CancellationException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicReference
import org.eclipse.lsp4j.Position as Lsp4jPosition
import org.eclipse.lsp4j.Range as Lsp4jRange

/**
 * Sends the client-to-server *requests* (as opposed to [ReqnrollNotificationSender]'s fire-and-forget
 * notifications). Every method blocks its calling thread until the response arrives or the timeout elapses, so
 * callers must invoke it from a background thread (e.g. inside a `Task.Backgroundable` or a pooled thread), never
 * directly from `AnAction.actionPerformed`'s EDT dispatch.
 *
 * Every request goes through the suspend `LspServer.sendRequest`, not `sendRequestSync`. The sync helper catches
 * the `ExecutionException` carrying the server's `ResponseErrorException`, logs it to `idea.log` and returns `null`,
 * so a server error, a timeout and an empty answer all looked the same (confirmed by decompiling Rider 2024.3.5;
 * the helper sorts four exception types and rethrows two, so it is deliberate platform behaviour). The suspend
 * `sendRequest` awaits the future through `kotlinx.coroutines.future.await`, which resumes with the real failure.
 * [send] wraps it and returns an [LspRequestResult], which keeps an answer apart from each kind of failure.
 *
 * [send] rebuilds the two things `sendRequestSync` provided: [runBlockingMaybeCancellable] keeps the wait responsive to
 * `ProgressManager.checkCanceled()`, and `withTimeoutOrNull` restores a per-request timeout, since `sendRequest` takes
 * none. On a timeout or cancellation it also cancels LSP4J's future, which sends `$/cancelRequest` to the server
 * as the sync helper did.
 *
 * [rename], [toggleComment] and [formatDocument] keep their own outcome types, because a `-32803` rejection there
 * carries a reason for the user (see [EditRejection]).
 */
object ReqnrollRequestSender {
    private const val FIND_UNUSED_TIMEOUT_MS = 30_000
    private const val FIND_USAGES_TIMEOUT_MS = 10_000
    private const val CODE_LENS_TIMEOUT_MS = 10_000
    private const val INLAY_HINT_TIMEOUT_MS = 10_000
    private const val ON_TYPE_FORMATTING_TIMEOUT_MS = 10_000
    private const val FOLDING_RANGE_TIMEOUT_MS = 10_000
    private const val DOCUMENT_LINK_TIMEOUT_MS = 10_000
    private const val FIND_HOOKS_TIMEOUT_MS = 10_000
    private const val FIND_MATCHING_SCENARIOS_TIMEOUT_MS = 10_000
    private const val TOGGLE_COMMENT_TIMEOUT_MS = 10_000
    private const val FORMAT_TIMEOUT_MS = 10_000
    private const val RENAME_TARGETS_TIMEOUT_MS = 10_000
    private const val RENAME_TIMEOUT_MS = 10_000
    private const val DOCUMENT_SYMBOL_TIMEOUT_MS = 10_000
    private const val DEFINITION_TIMEOUT_MS = 10_000
    private const val RESOLVE_TEST_TARGETS_TIMEOUT_MS = 10_000
    private const val RESOLVE_CONTAINER_TEST_TARGETS_TIMEOUT_MS = 10_000
    private const val REGISTER_TEST_RUN_TIMEOUT_MS = 10_000
    private const val GET_TEST_OUTCOME_TIMEOUT_MS = 10_000

    /** Runs `reqnroll/findUnusedStepDefinitions`. */
    fun findUnusedStepDefinitions(project: Project): LspRequestResult<FindUnusedStepDefinitionsResponse> =
        send(project, "findUnusedStepDefinitions", FIND_UNUSED_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).findUnusedStepDefinitions(ReqnrollEmptyParams())
        }

    /** Runs `reqnroll/findStepUsages` for the binding at (uri, line, character). */
    fun findStepUsages(project: Project, uri: String, line: Int, character: Int): LspRequestResult<FindStepUsagesResponse> {
        val params = ReferenceParams().apply {
            textDocument = TextDocumentIdentifier(uri)
            position = Lsp4jPosition(line, character)
            context = ReferenceContext(false)
        }
        return send(project, "findStepUsages", FIND_USAGES_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).findStepUsages(params)
        }
    }

    /**
     * Runs the *standard* `textDocument/codeLens` request (step-usage-count lenses for `.cs`
     * files — see StepCodeLensHandler.cs). Standard LSP methods are already declared on LSP4J's
     * base `LanguageServer`/`TextDocumentService` interfaces, so no custom `@JsonRequest` method
     * or cast to `ReqnrollLanguageServer` is needed, unlike the custom reqnroll-prefixed methods above.
     */
    fun codeLens(project: Project, uri: String): LspRequestResult<List<CodeLens>> {
        val params = CodeLensParams(TextDocumentIdentifier(uri))
        return send(project, "codeLens", CODE_LENS_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.codeLens(params)
        }.map { it?.filterNotNull() }
    }

    /** Runs the *standard* `textDocument/inlayHint` request (binding-info hints for `.feature` files — see FeatureInlayHintHandler.cs) covering the given line range. */
    fun inlayHint(project: Project, uri: String, startLine: Int, endLine: Int): LspRequestResult<List<InlayHint>> {
        val range = Lsp4jRange(Lsp4jPosition(startLine, 0), Lsp4jPosition(endLine, Int.MAX_VALUE))
        val params = InlayHintParams(TextDocumentIdentifier(uri), range)
        return send(project, "inlayHint", INLAY_HINT_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.inlayHint(params)
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
    ): LspRequestResult<List<TextEdit>> {
        val params = DocumentOnTypeFormattingParams(
            TextDocumentIdentifier(uri),
            FormattingOptions(tabSize, insertSpaces),
            Lsp4jPosition(line, character),
            triggerChar,
        )
        return send(project, "onTypeFormatting", ON_TYPE_FORMATTING_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.onTypeFormatting(params)
        }.map { it?.filterNotNull() }
    }

    /**
     * Runs the *standard* `textDocument/foldingRange` request (Code Folding for `.feature`
     * files — see FeatureFoldingRangeHandler.cs). Standard LSP method, so — like [codeLens] and
     * [inlayHint] — no custom `@JsonRequest` method or cast to `ReqnrollLanguageServer` is needed.
     */
    fun foldingRange(project: Project, uri: String): LspRequestResult<List<FoldingRange>> {
        val params = FoldingRangeRequestParams(TextDocumentIdentifier(uri))
        return send(project, "foldingRange", FOLDING_RANGE_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.foldingRange(params)
        }.map { it?.filterNotNull() }
    }

    /**
     * Runs the *standard* `textDocument/documentLink` request (clickable tags for `.feature` files -
     * see DocumentLinkHandler.cs). Standard LSP method, so - like [foldingRange] - no custom `@JsonRequest`
     * method or cast to `ReqnrollLanguageServer` is needed. Rider's generic client never sends it itself.
     */
    fun documentLink(project: Project, uri: String): LspRequestResult<List<DocumentLink>> {
        val params = DocumentLinkParams(TextDocumentIdentifier(uri))
        return send(project, "documentLink", DOCUMENT_LINK_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.documentLink(params)
        }.map { it?.filterNotNull() }
    }

    /**
     * Runs `reqnroll/findHooks` for the position (uri, line, character) in a `.feature` file.
     * [ownLevelOnly] is forwarded from the hook-count CodeVision lens's `command.arguments`
     * (see HookCodeVisionProvider) so the response matches exactly what the lens counted; manual
     * invocations (GoToHooksAction) leave it at the default `false` (cumulative).
     */
    fun findHooks(
        project: Project, uri: String, line: Int, character: Int, ownLevelOnly: Boolean = false,
    ): LspRequestResult<FindHooksResponse> {
        val params = FindHooksRequestParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character), ownLevelOnly)
        return send(project, "findHooks", FIND_HOOKS_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).findHooks(params)
        }
    }

    /** Runs `reqnroll/findMatchingScenarios` for the hook-binding attribute at (uri, line, character) in a `.cs` file. */
    fun findMatchingScenarios(
        project: Project, uri: String, line: Int, character: Int,
    ): LspRequestResult<FindMatchingScenariosResponse> {
        val params = TextDocumentPositionParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character))
        return send(project, "findMatchingScenarios", FIND_MATCHING_SCENARIOS_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).findMatchingScenarios(params)
        }
    }

    /**
     * Runs the *standard* `workspace/executeCommand` request for `reqnroll.toggleComment`
     * (Comment/Uncomment toggle — see CommentToggleHandler.cs). There is no dedicated,
     * reqnroll-prefixed custom method for this feature, unlike findStepUsages/findHooks — the server responds
     * by sending a `workspace/applyEdit` *request back to the client*, which Rider's platform
     * `Lsp4jClient.applyEdit` already applies natively (confirmed by decompiling — it's a `final`
     * method on the base class, not something [ReqnrollLspServerDescriptor.createLsp4jClient]'s
     * wrapping needs to add a consumer for), so this call's own return value is unused; callers
     * only care whether the command was dispatched, or rejected with a reason to show (`-32803`, for
     * example when the document is not open in the server).
     */
    fun toggleComment(project: Project, uri: String, startLine: Int, endLine: Int): ToggleCommentOutcome {
        val params = ExecuteCommandParams("reqnroll.toggleComment", listOf(uri, startLine, endLine))
        return toggleCommentOutcome(
            send(project, "toggleComment", TOGGLE_COMMENT_TIMEOUT_MS) { languageServer ->
                languageServer.workspaceService.executeCommand(params)
            },
        )
    }

    /**
     * Maps a `reqnroll.toggleComment` result to its outcome: a server rejection (`-32803`) to
     * [ToggleCommentOutcome.Rejected] (the message is shown to the user), any other failure to
     * [ToggleCommentOutcome.Failed] (logged only). `internal` so it is testable without a live server.
     */
    internal fun toggleCommentOutcome(result: LspRequestResult<*>): ToggleCommentOutcome {
        if (result is LspRequestResult.Success) return ToggleCommentOutcome.Dispatched
        return EditRejection.messageOf(result)?.let { ToggleCommentOutcome.Rejected(it) } ?: ToggleCommentOutcome.Failed
    }

    /**
     * Runs the standard `textDocument/formatting` request for [uri] on behalf of
     * [net.reqnroll.idesupport.rider.formatting.ReqnrollFormattingService]. A `-32803` rejection reaches the caller
     * as [FormatOutcome.Rejected].
     */
    fun formatDocument(project: Project, uri: String, tabSize: Int, insertSpaces: Boolean): FormatOutcome {
        val params = DocumentFormattingParams(
            TextDocumentIdentifier(uri),
            FormattingOptions(tabSize, insertSpaces),
        )
        return formatOutcome(
            send(project, "formatDocument", FORMAT_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.formatting(params)
            },
        )
    }

    /** Maps a `textDocument/formatting` result to its outcome; `internal` so it is testable without a live server. */
    internal fun formatOutcome(result: LspRequestResult<List<TextEdit?>>): FormatOutcome {
        if (result is LspRequestResult.Success) return FormatOutcome.Edits(result.value.orEmpty().filterNotNull())
        return EditRejection.messageOf(result)?.let { FormatOutcome.Rejected(it) } ?: FormatOutcome.Failed
    }

    /** True when a Reqnroll LSP server is running for [project]. */
    fun hasRunningServer(project: Project): Boolean =
        LspServerManager.getInstance(project)
            .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)
            .isNotEmpty()

    /** Runs `reqnroll/renameTargets` for the position (uri, line, character). */
    fun renameTargets(project: Project, uri: String, line: Int, character: Int): LspRequestResult<RenameTargetsResponse> {
        val params = TextDocumentPositionParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character))
        return send(project, "renameTargets", RENAME_TARGETS_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).renameTargets(params)
        }
    }

    /**
     * Runs the *standard* `textDocument/rename` request with [newName] at (uri, line, character).
     * Standard LSP method, so — like [codeLens]/[foldingRange] — no custom `@JsonRequest` method
     * or cast to `ReqnrollLanguageServer` is needed. Rider has no native rename bridge (confirmed
     * by decompiling `LspServerDescriptor` — no `lspRenameSupport`-style customization exists),
     * so callers must apply the returned `WorkspaceEdit` themselves; see `RenameWorkspaceEditApplier`.
     *
     * Returns [RenameOutcome.Rejected] with the server's own human-readable reason when the server
     * rejects the rename with `-32803` — e.g. a step-rename validation failure. A rejected rename
     * follows a modal "pick a target, type a new expression" flow the user just completed, so
     * [RenameStepRunner] must explain why nothing happened.
     */
    fun rename(project: Project, uri: String, line: Int, character: Int, newName: String): RenameOutcome {
        val params = RenameParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character), newName)
        return renameOutcome(
            send(project, "rename", RENAME_TIMEOUT_MS) { languageServer ->
                languageServer.textDocumentService.rename(params)
            },
        )
    }

    /**
     * Maps a `textDocument/rename` result to its outcome. Only a server rejection (`-32803`)
     * carries a reason meant for the user, as [RenameOutcome.Rejected]; any other
     * failure — including a JSON-RPC error with another code — is unexpected, so it is shown only as a
     * generic [RenameOutcome.Failed] message. `internal` so it is testable without a live server.
     */
    internal fun renameOutcome(result: LspRequestResult<WorkspaceEdit>): RenameOutcome = when (result) {
        is LspRequestResult.Success ->
            result.value?.let { RenameOutcome.Success(it) } ?: RenameOutcome.Failed(GENERIC_RENAME_FAILURE)
        LspRequestResult.Unavailable -> RenameOutcome.Failed("The Reqnroll LSP server is not running or did not respond.")
        is LspRequestResult.TimedOut -> RenameOutcome.Failed("The rename request timed out.")
        else -> EditRejection.messageOf(result)?.let { RenameOutcome.Rejected(it) }
            ?: RenameOutcome.Failed(GENERIC_RENAME_FAILURE)
    }

    private const val GENERIC_RENAME_FAILURE = "Rename failed — the new expression may be invalid, or nothing to rename."

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
    fun documentSymbol(project: Project, uri: String): LspRequestResult<List<DocumentSymbol>> {
        val params = DocumentSymbolParams(TextDocumentIdentifier(uri))
        return send(project, "documentSymbol", DOCUMENT_SYMBOL_TIMEOUT_MS, quiet = true) { languageServer ->
            languageServer.textDocumentService.documentSymbol(params)
        }.map { symbols -> symbols?.mapNotNull { it?.right } }
    }

    /**
     * Runs the *standard* `textDocument/definition` request for a `.feature` step (see DefinitionHandler.cs): the
     * step definition methods the step binds to, each pointing at the method's name. Empty for an undefined step
     * or one whose binding has no source on this machine; a JSON `null` answer is also mapped to an empty list.
     */
    fun definition(project: Project, uri: String, line: Int, character: Int): LspRequestResult<List<Location>> {
        val params = DefinitionParams(TextDocumentIdentifier(uri), Lsp4jPosition(line, character))
        return send(project, "definition", DEFINITION_TIMEOUT_MS) { languageServer ->
            languageServer.textDocumentService.definition(params)
        }.map { result ->
            when {
                result == null -> emptyList()
                result.isLeft -> result.left.filterNotNull()
                else -> result.right.filterNotNull().map { Location(it.targetUri, it.targetSelectionRange) }
            }
        }
    }

    /**
     * Runs `reqnroll/resolveTestTargets` for the range [(startLine, startChar), (endLine, endChar)]
     * in a `.feature` file (Test-Runner-Integration-Design ("Mapping layer" and "LSP messages")) — resolves the generated test method(s)
     * the scenario/Outline/example row at that range corresponds to.
     */
    fun resolveTestTargets(
        project: Project, uri: String, startLine: Int, startChar: Int, endLine: Int, endChar: Int,
    ): LspRequestResult<ResolveTestTargetsResponse> {
        val params = ResolveTestTargetsParams(
            TextDocumentIdentifier(uri),
            Lsp4jRange(Lsp4jPosition(startLine, startChar), Lsp4jPosition(endLine, endChar)),
        )
        return send(project, "resolveTestTargets", RESOLVE_TEST_TARGETS_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).resolveTestTargets(params)
        }
    }

    /**
     * Runs `reqnroll/resolveContainerTestTargets` for the full-body range of a Feature/Rule
     * container [(startLine, startChar), (endLine, endChar)] in a `.feature` file
     * ("Run scenarios") — resolves the generated test method(s) for every Scenario/Outline the
     * container holds, in one call.
     */
    fun resolveContainerTestTargets(
        project: Project, uri: String, startLine: Int, startChar: Int, endLine: Int, endChar: Int,
    ): LspRequestResult<ResolveContainerTestTargetsResponse> {
        val params = ResolveContainerTestTargetsParams(
            TextDocumentIdentifier(uri),
            Lsp4jRange(Lsp4jPosition(startLine, startChar), Lsp4jPosition(endLine, endChar)),
        )
        return send(project, "resolveContainerTestTargets", RESOLVE_CONTAINER_TEST_TARGETS_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).resolveContainerTestTargets(params)
        }
    }

    /**
     * Runs `reqnroll/testOutcomes/registerRun` (LSP-server outcome pipeline). [RunTestRunner]
     * [net.reqnroll.idesupport.rider.testrunner.RunTestRunner] treats any failure the same as
     * [RegisterTestRunResponse.success] `false`: fall back to the TRX-only path for this run.
     */
    fun registerTestRun(project: Project): LspRequestResult<RegisterTestRunResponse> =
        send(project, "registerTestRun", REGISTER_TEST_RUN_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).registerTestRun(ReqnrollEmptyParams())
        }

    /**
     * Runs `reqnroll/testOutcomes/getOutcome` for one generated test method (LSP-server outcome
     * pipeline). [assemblyPath] must be the compiled test container path, matching
     * what the bundled VSTest logger reports as `TestCase.Source` — not the `.csproj` path.
     */
    fun getTestOutcome(
        project: Project, assemblyPath: String, typeFullName: String, methodName: String,
    ): LspRequestResult<GetTestOutcomeResponse> {
        val params = GetTestOutcomeParams(assemblyPath, typeFullName, methodName)
        return send(project, "getTestOutcome", GET_TEST_OUTCOME_TIMEOUT_MS) { languageServer ->
            (languageServer as ReqnrollLanguageServer).getTestOutcome(params)
        }
    }

    /**
     * Sends one request to the first running Reqnroll LSP server and waits up to [timeoutMs] for the answer.
     * Failures are logged as warnings, or at verbose level when [quiet] is set (for requests the editor sends on
     * every open or edit, whose failures are routine while the server starts). Blocks; call off the EDT.
     */
    private fun <T> send(
        project: Project,
        name: String,
        timeoutMs: Int,
        quiet: Boolean = false,
        request: (LanguageServer) -> CompletableFuture<T>,
    ): LspRequestResult<T> {
        val server = firstRunningServer(project) ?: return LspRequestResult.Unavailable
        val pending = AtomicReference<CompletableFuture<*>?>()
        return try {
            // withTimeoutOrNull, not withTimeout: runBlockingMaybeCancellable rethrows any CancellationException that
            // escapes the block, a TimeoutCancellationException included, as a ProcessCanceledException.
            val answer = runBlockingMaybeCancellable {
                withTimeoutOrNull(timeoutMs.toLong()) {
                    Answer(server.sendRequest { languageServer -> request(languageServer).also { pending.set(it) } })
                }
            }
            if (answer != null) {
                LspRequestResult.Success(answer.value)
            } else {
                pending.get()?.cancel(true) // LSP4J sends $/cancelRequest
                log(quiet, "$name: request timed out after ${timeoutMs}ms", null)
                LspRequestResult.TimedOut(timeoutMs)
            }
        } catch (ex: ProcessCanceledException) {
            pending.get()?.cancel(true)
            throw ex
        } catch (ex: CancellationException) {
            pending.get()?.cancel(true)
            throw ex
        } catch (ex: Exception) {
            val result = LspRequestResult.fromException(ex)
            when {
                // A user-correctable rejection: info, so the Reqnroll tool window does not pop up for it.
                result is LspRequestResult.ServerError && result.code == EditRejection.ERROR_CODE ->
                    ReqnrollDebugLogger.info("$name: rejected by server: ${result.message}")
                // An error *response* is the server doing its job, so no stack trace.
                result is LspRequestResult.ServerError ->
                    log(quiet, "$name: server answered with error ${result.code}: ${result.message}", null)
                else -> log(quiet, "$name: request failed", ex)
            }
            result
        }
    }

    /** Wraps an answer so a `null` answer stays apart from `withTimeoutOrNull`'s `null` for a timeout. */
    private class Answer<T>(val value: T?)

    private fun log(quiet: Boolean, message: String, ex: Throwable?) {
        if (quiet) ReqnrollDebugLogger.verbose(message, ex) else ReqnrollDebugLogger.warn(message, ex)
    }

    private fun firstRunningServer(project: Project) =
        LspServerManager.getInstance(project)
            .getServersForProvider(ReqnrollLspServerSupportProvider::class.java)
            .firstOrNull()
            .also {
                if (it == null)
                    ReqnrollDebugLogger.verbose("ReqnrollRequestSender: no Reqnroll LSP server running")
            }
}

/**
 * Outcome of [ReqnrollRequestSender.rename]: either a [WorkspaceEdit] to apply, or a reason it
 * couldn't be produced. Distinguishes "the server rejected this rename for an explainable reason"
 * from every other failure, so
 * [net.reqnroll.idesupport.rider.actions.RenameStepRunner] can show the server's actual message.
 */
sealed interface RenameOutcome {
    data class Success(val edit: WorkspaceEdit) : RenameOutcome

    /** The server rejected the rename with a reason the user can fix (`-32803`); [message] is shown verbatim. */
    data class Rejected(val message: String) : RenameOutcome

    /** Any other failure; [message] is generic. */
    data class Failed(val message: String) : RenameOutcome
}

/**
 * Outcome of [ReqnrollRequestSender.toggleComment]: [Dispatched] once the server accepted the command
 * (the edit itself arrives separately via `workspace/applyEdit`), [Rejected] when it refused with a
 * reason to show the user (`-32803`), or [Failed] for anything else (logged only).
 */
sealed interface ToggleCommentOutcome {
    data object Dispatched : ToggleCommentOutcome
    data class Rejected(val message: String) : ToggleCommentOutcome
    data object Failed : ToggleCommentOutcome
}

/**
 * Outcome of [ReqnrollRequestSender.formatDocument]: the [Edits] to apply (possibly none), a [Rejected]
 * with a reason the server wants the user to see (`-32803`), or [Failed] for
 * anything else (logged only).
 */
sealed interface FormatOutcome {
    data class Edits(val edits: List<TextEdit>) : FormatOutcome
    data class Rejected(val message: String) : FormatOutcome
    data object Failed : FormatOutcome
}
