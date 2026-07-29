package com.reqnroll.ide.rider.lsp.protocol

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.TextDocumentIdentifier

/**
 * Wire DTOs for the reqnroll-prefixed client-to-server notifications, mirrored field-for-field
 * (camelCase, matching the server's CamelCasePropertyNamesContractResolver) against:
 *   - src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/ReqnrollProjectLoadedParams.cs
 *   - src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/ReqnrollProjectFilesParams.cs
 *   - src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/ReqnrollProjectUnloadedParams.cs
 *   - src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/DocumentActivated/DocumentActivatedParams.cs
 *
 * `kind`/`role` are plain Int (not Kotlin enums) matching the C# side's default Newtonsoft
 * integer enum representation — LSP4J's Gson-based serializer defaults to enum-name-as-string,
 * which would not match the wire format VS/VS Code already send (see VsProjectEventMonitor.cs's
 * `kind = 1` / `role = 1` literal int usage). ProjectFilesKind/ProjectFileRole below are just
 * named int constants for callers to use instead of magic numbers.
 */

/** Payload for `reqnroll/projectLoaded`. */
data class ReqnrollProjectLoadedParams(
    val workspaceFolder: String,
    val projectFile: String,
    val projectFolder: String,
    val outputAssemblyPath: String,
    val targetFrameworkMoniker: String,
    val defaultNamespace: String,
    val packageReferences: List<PackageReferenceInfo> = emptyList(),
)

/** One resolved NuGet package reference. */
data class PackageReferenceInfo(
    val packageId: String,
    val version: String,
    val installPath: String,
)

/** Payload for `reqnroll/projectUnloaded`. */
data class ReqnrollProjectUnloadedParams(
    val projectFile: String,
)

/** Payload for `reqnroll/projectFiles`. `kind`/`files[].role` — see [ProjectFilesKind]/[ProjectFileRole]. */
data class ReqnrollProjectFilesParams(
    val projectFile: String,
    val targetFrameworkMoniker: String,
    val kind: Int,
    val files: List<ProjectFileEntry> = emptyList(),
)

/** One file attributed to a project in a [ReqnrollProjectFilesParams] payload. */
data class ProjectFileEntry(
    val path: String,
    val role: Int,
    val added: Boolean = true,
)

/** Matches `ProjectFilesKind` in ReqnrollProjectFilesParams.cs. */
object ProjectFilesKind {
    const val BASELINE = 0
    const val DELTA = 1
}

/** Matches `ProjectFileRole` in ReqnrollProjectFilesParams.cs. */
object ProjectFileRole {
    const val FEATURE = 0
    const val BINDING = 1

    /** Mirrors VsProjectEventMonitor.ClassifyRole — null for extensions the index doesn't track. */
    fun classify(path: String): Int? = when {
        path.endsWith(".feature", ignoreCase = true) -> FEATURE
        path.endsWith(".cs", ignoreCase = true) -> BINDING
        else -> null
    }
}

/** Payload for `reqnroll/documentActivated` (issue #85). */
data class DocumentActivatedParams(
    val uri: String,
)

/** Params for `reqnroll/findUnusedStepDefinitions` — the request takes no data, but LSP4J's `@JsonRequest` needs a params argument; matches the server's empty `FindUnusedStepDefinitionsParams` class. */
class ReqnrollEmptyParams

/** Response for `reqnroll/findUnusedStepDefinitions` — mirrors FindUnusedStepDefinitionsResponse.cs field-for-field. */
data class FindUnusedStepDefinitionsResponse(
    val items: List<UnusedStepDefinitionItem> = emptyList(),
)

/** One step-definition binding with zero matching steps across the workspace. */
data class UnusedStepDefinitionItem(
    val projectName: String? = null,
    val className: String? = null,
    val methodName: String? = null,
    val bindingExpression: String? = null,
    val sourceFile: String? = null,
    val sourceLine: Int = 0,
    val sourceChar: Int = 0,
)

/**
 * Response for `reqnroll/findStepUsages` — mirrors FindStepUsagesResponse.cs field-for-field,
 * including the three-state contract: [isBinding] false means the queried position isn't a
 * step-definition binding at all (caller should fall back to built-in C# Find Usages);
 * [isBinding] true with an empty [locations] means the binding genuinely has zero usages.
 */
data class FindStepUsagesResponse(
    val isBinding: Boolean = false,
    val locations: List<FindStepUsageItem> = emptyList(),
)

/** One step-usage location within a feature file. */
data class FindStepUsageItem(
    val uri: String = "",
    val startLine: Int = 0,
    val startChar: Int = 0,
    val endLine: Int = 0,
    val endChar: Int = 0,
    val stepText: String? = null,
    val keyword: String? = null,
    val scenarioName: String? = null,
    val projectName: String? = null,
    val featureName: String? = null,
    val ruleName: String? = null,
)

/**
 * Params for `reqnroll/goToHooks` — mirrors GoToHooksParams.cs field-for-field. `ownLevelOnly`
 * is set only by CodeLens-sourced invocations (HookCodeVisionProvider, forwarding the
 * server-supplied `command.arguments` flag) so the response matches exactly what the lens
 * counted, rather than the fuller cumulative list a manual "Go to Hooks" invocation returns.
 */
data class GoToHooksRequestParams(
    val textDocument: TextDocumentIdentifier,
    val position: Position,
    val ownLevelOnly: Boolean = false,
)

/** Response for `reqnroll/goToHooks` — mirrors GoToHooksResponse.cs field-for-field. */
data class GoToHooksResponse(
    val hooks: List<GoToHookLocation> = emptyList(),
)

/** One hook binding applicable at the queried `.feature` file position. */
data class GoToHookLocation(
    val uri: String = "",
    val startLine: Int = 0,
    val startChar: Int = 0,
    val hookType: String = "",
    val hookOrder: Int = 0,
    val methodName: String = "",
)

/** Response for `reqnroll/goToMatchingScenarios` (issue #373) — mirrors GoToMatchingScenariosResponse.cs field-for-field. */
data class GoToMatchingScenariosResponse(
    val scenarios: List<MatchingScenarioLocation> = emptyList(),
)

/** One scenario matched by the queried hook binding's scope. */
data class MatchingScenarioLocation(
    val uri: String = "",
    val startLine: Int = 0,
    val startChar: Int = 0,
    val scenarioName: String = "",
    val isOutline: Boolean = false,
)

/** Response for `reqnroll/renameTargets` — mirrors RenameTargetsResponse.cs field-for-field. */
data class RenameTargetsResponse(
    val targets: List<RenameTargetItem> = emptyList(),
)

/** One renameable binding attribute at the queried position. */
data class RenameTargetItem(
    val label: String = "",
    val expression: String = "",
    val attributeIndex: Int = 0,
    val startLine: Int = 0,
    val startChar: Int = 0,
    val endLine: Int = 0,
    val endChar: Int = 0,
)

/** Params for `reqnroll/selectRenameTarget` — mirrors SelectRenameTargetParams.cs field-for-field. */
data class SelectRenameTargetParams(
    val uri: String = "",
    val version: Int = 0,
    val attributeIndex: Int = 0,
)
