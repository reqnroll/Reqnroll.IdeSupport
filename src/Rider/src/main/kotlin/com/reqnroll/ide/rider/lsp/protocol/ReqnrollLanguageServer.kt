package com.reqnroll.ide.rider.lsp.protocol

import org.eclipse.lsp4j.ReferenceParams
import org.eclipse.lsp4j.TextDocumentPositionParams
import org.eclipse.lsp4j.jsonrpc.services.JsonNotification
import org.eclipse.lsp4j.jsonrpc.services.JsonRequest
import org.eclipse.lsp4j.services.LanguageServer
import java.util.concurrent.CompletableFuture

/**
 * Custom LSP4J server interface adding the Reqnroll protocol extensions
 * (src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs) that the platform's
 * generic LSP client has no built-in way to send. Wired in via
 * ReqnrollLspServerDescriptor.lsp4jServerClass; see
 * docs/Rider-Project-Document-Sync-Implementation-Plan.md §3.1 for how the resulting typed
 * proxy is obtained and called (LspServerManager + LspServer.sendNotification), confirmed
 * against Rider 2024.3.5's actual bundled classes, not just JetBrains' docs.
 *
 * `@JsonRequest` methods (as opposed to `@JsonNotification`) are the request/response
 * counterpart — called via `LspServer.sendRequest`/`sendRequestSync` instead of
 * `sendNotification`, confirmed to exist on Rider 2024.3.5's actual `LspServer` interface by
 * decompiling `com.intellij.platform.lsp.api.LspServer`.
 */
interface ReqnrollLanguageServer : LanguageServer {
    @JsonNotification("reqnroll/projectLoaded")
    fun projectLoaded(params: ReqnrollProjectLoadedParams)

    @JsonNotification("reqnroll/projectUnloaded")
    fun projectUnloaded(params: ReqnrollProjectUnloadedParams)

    @JsonNotification("reqnroll/projectFiles")
    fun projectFiles(params: ReqnrollProjectFilesParams)

    @JsonNotification("reqnroll/documentActivated")
    fun documentActivated(params: DocumentActivatedParams)

    /** Find Unused Step Definitions (F15) — scans the whole workspace, no params. */
    @JsonRequest("reqnroll/findUnusedStepDefinitions")
    fun findUnusedStepDefinitions(params: ReqnrollEmptyParams): CompletableFuture<FindUnusedStepDefinitionsResponse>

    /**
     * Find Step Definition Usages — params are the standard `textDocument/references` shape
     * (`ReferenceParams`, an existing LSP4J class) even though this is a custom method name; the
     * server registers it that way deliberately (see FindStepUsagesHandler.cs) since it needs a
     * third "not a binding" state generic `textDocument/references` can't express.
     */
    @JsonRequest("reqnroll/findStepUsages")
    fun findStepUsages(params: ReferenceParams): CompletableFuture<FindStepUsagesResponse>

    /**
     * Hook Navigation ("Go to Hooks") — returns the hook bindings applicable at a `.feature`
     * file position (context level Feature/Scenario/Step, tag/scope-filtered — see
     * GoToHooksHandler.cs). A separate custom message from `textDocument/definition` because
     * that message is already used by Go to Step Definition on step lines.
     */
    @JsonRequest("reqnroll/goToHooks")
    fun goToHooks(params: TextDocumentPositionParams): CompletableFuture<GoToHooksResponse>

    /**
     * Hook-match-count CodeLens click action (issue #373) — the inverse of [goToHooks]: returns
     * every scenario, across the whole owning project(s), that the hook binding at a `.cs` file
     * position matches (see GoToMatchingScenariosHandler.cs).
     */
    @JsonRequest("reqnroll/goToMatchingScenarios")
    fun goToMatchingScenarios(params: TextDocumentPositionParams): CompletableFuture<GoToMatchingScenariosResponse>

    /**
     * Step Rename disambiguation (issue #160) — returns every candidate binding attribute
     * renameable at a `.feature` or `.cs` file position (see RenameTargetsHandler.cs). Standard
     * `textDocument/prepareRename`/`textDocument/rename` handle the mechanical edit; this custom
     * request is only for picking which binding to rename when the position is ambiguous.
     */
    @JsonRequest("reqnroll/renameTargets")
    fun renameTargets(params: TextDocumentPositionParams): CompletableFuture<RenameTargetsResponse>

    /**
     * Records which candidate binding a `reqnroll/renameTargets` disambiguation picked, for the
     * `textDocument/rename` call that follows to pick up (see StepRenameHandler.HandleRenameAsync's
     * session-first resolution order via RenameBindingResolver.ResolveBindingForRename).
     */
    @JsonNotification("reqnroll/selectRenameTarget")
    fun selectRenameTarget(params: SelectRenameTargetParams)
}
