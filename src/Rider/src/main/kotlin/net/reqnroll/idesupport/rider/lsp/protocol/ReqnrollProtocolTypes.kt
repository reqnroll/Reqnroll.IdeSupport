package net.reqnroll.idesupport.rider.lsp.protocol

import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.Range
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

/**
 * One step-definition binding with zero matching steps across the workspace.
 *
 * [sourceFile] is null when the binding's source does not exist on this machine — the assembly was
 * built elsewhere (a container, a CI agent, another machine, an external binding package) and the
 * path it recorded could not be mapped onto this workspace. [isResolved] says so explicitly and
 * [recordedSourceFile] carries the path the assembly does record, which is the only thing that can
 * explain the entry to a user. Older servers omit both fields, and the defaults below keep those
 * behaving exactly as before.
 */
data class UnusedStepDefinitionItem(
    val projectName: String? = null,
    val className: String? = null,
    val methodName: String? = null,
    val bindingExpression: String? = null,
    val sourceFile: String? = null,
    val sourceLine: Int = 0,
    val sourceChar: Int = 0,
    val isResolved: Boolean = true,
    val recordedSourceFile: String? = null,
    /**
     * The binding's step keyword — `Given`, `When` or `Then` — shown as the attribute name
     * (issue #757). Null when unknown or from an older server. A `[StepDefinition]` attribute is
     * registered as one binding per keyword, so it reports whichever of the three this one is.
     */
    val stepDefinitionType: String? = null,
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
 * Params for `reqnroll/findHooks` — mirrors FindHooksParams.cs field-for-field. `ownLevelOnly`
 * is set only by CodeLens-sourced invocations (HookCodeVisionProvider, forwarding the
 * server-supplied `command.arguments` flag) so the response matches exactly what the lens
 * counted, rather than the fuller cumulative list a manual "Go to Hooks" invocation returns.
 */
data class FindHooksRequestParams(
    val textDocument: TextDocumentIdentifier,
    val position: Position,
    val ownLevelOnly: Boolean = false,
)

/** Response for `reqnroll/findHooks` — mirrors FindHooksResponse.cs field-for-field. */
data class FindHooksResponse(
    val hooks: List<FindHookLocation> = emptyList(),
)

/** One hook binding applicable at the queried `.feature` file position. */
data class FindHookLocation(
    val uri: String = "",
    val startLine: Int = 0,
    val startChar: Int = 0,
    val hookType: String = "",
    val hookOrder: Int = 0,
    val methodName: String = "",
)

/** Response for `reqnroll/findMatchingScenarios` (issue #373) — mirrors FindMatchingScenariosResponse.cs field-for-field. */
data class FindMatchingScenariosResponse(
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
    /**
     * The position the disambiguation was invoked at — the same one passed to
     * `reqnroll/renameTargets`. Lets the server resolve [attributeIndex] to the binding it denotes
     * while that candidate list is still current, instead of carrying a bare index across the
     * modal dialog and re-applying it to a list rebuilt later (issue #671, R5).
     */
    val position: Position? = null,
)

/**
 * Params for `reqnroll/renameApplied` — mirrors RenameAppliedParams.cs field-for-field.
 *
 * Reports whether this client actually applied the `WorkspaceEdit` returned from
 * `textDocument/rename`. The server stages the binding-registry and match-cache updates the edit
 * implies and commits them only on [applied] `true`, so a rename we decline to apply (see
 * [net.reqnroll.idesupport.rider.actions.RenameStepRunner]'s staleness check) no longer leaves the
 * registry describing a step expression present in no file — issue #670, fixed under #671 R3.
 * Must be sent either way, so the server can drop staged updates promptly instead of holding them.
 */
data class RenameAppliedParams(
    val uri: String = "",
    val applied: Boolean = false,
)

/**
 * Params for `reqnroll/resolveTestTargets` — mirrors ResolveTestTargetsParams.cs field-for-field
 * (design doc §3/§4, issue #262). A range within a scenario/Outline's own header or steps resolves
 * to every target for that scenario; a range within one specific `Examples:` row resolves to just
 * that row's target.
 */
data class ResolveTestTargetsParams(
    val textDocument: TextDocumentIdentifier,
    val range: Range,
)

/** Response for `reqnroll/resolveTestTargets` — mirrors ResolveTestTargetsResponse.cs field-for-field. */
data class ResolveTestTargetsResponse(
    val targets: List<ScenarioTestTargetItem> = emptyList(),
)

/** One generated test method (or one row of a row-tests-parameterized method) a scenario/row resolves to. Mirrors ScenarioTestTargetDto.cs field-for-field. */
data class ScenarioTestTargetItem(
    val declaringTypeFullName: String = "",
    val methodName: String = "",
    val isParameterized: Boolean = false,
    val rowArguments: Map<String, String>? = null,
    val rowIndex: Int? = null,
)

/**
 * Params for `reqnroll/resolveContainerTestTargets` — mirrors ResolveContainerTestTargetsParams.cs
 * field-for-field (issue #744, "Run scenarios" on Feature/Rule blocks). [range] is the container's
 * *full body* range (not its header line alone), matching what the server expects: every
 * Scenario/Outline fully contained within it is resolved, including Rule-nested scenarios when the
 * range is the enclosing Feature's.
 */
data class ResolveContainerTestTargetsParams(
    val textDocument: TextDocumentIdentifier,
    val range: Range,
)

/**
 * Response for `reqnroll/resolveContainerTestTargets` — mirrors
 * ResolveContainerTestTargetsResponse.cs field-for-field (issue #744, "Run scenarios"); shares
 * [ScenarioTestTargetItem] with [ResolveTestTargetsResponse], so one target looks the same whether
 * it came from a single-scenario or a whole-container resolution.
 */
data class ResolveContainerTestTargetsResponse(
    val targets: List<ScenarioTestTargetItem> = emptyList(),
)

/** Response for `reqnroll/testOutcomes/registerRun` (takes [ReqnrollEmptyParams] — see its doc comment) — mirrors RegisterTestRunResponse.cs field-for-field. [success] false means "the server couldn't start its loopback listener"; the caller falls back to its own execution's TRX output. */
data class RegisterTestRunResponse(
    val success: Boolean = false,
    val runId: String? = null,
    val endpoint: String? = null,
)

/** Params for `reqnroll/testOutcomes/getOutcome` — mirrors GetTestOutcomeParams.cs field-for-field. [assemblyPath] must match the *compiled* test container path (the same value vstest reports as `TestCase.Source`), not the `.csproj` path. */
data class GetTestOutcomeParams(
    val assemblyPath: String = "",
    val typeFullName: String = "",
    val methodName: String = "",
)

/** Response for `reqnroll/testOutcomes/getOutcome` — mirrors GetTestOutcomeResponse.cs field-for-field. [found] false means "no run reported this method this session"; the caller falls back to TRX. */
data class GetTestOutcomeResponse(
    val found: Boolean = false,
    val aggregate: String = "",
    val rows: List<TestOutcomeRowItem> = emptyList(),
    val isRunning: Boolean = false,
    val isStale: Boolean = false,
)

/** One row (test case) of a method's last-known outcome — mirrors TestOutcomeRowDto.cs field-for-field. */
data class TestOutcomeRowItem(
    val displayName: String = "",
    val outcome: String = "",
    val durationMs: Double = 0.0,
    val errorMessage: String? = null,
    val stepCount: Int = 0,
    val failedStepIndex: Int? = null,
    val failedStepText: String? = null,
    val failedStepOutcome: String? = null,
)
