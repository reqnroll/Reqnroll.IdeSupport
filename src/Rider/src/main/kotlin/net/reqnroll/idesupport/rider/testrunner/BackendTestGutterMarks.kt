package net.reqnroll.idesupport.rider.testrunner

import com.intellij.openapi.util.registry.Registry
import net.reqnroll.idesupport.rider.ReqnrollPlugin
import java.nio.file.Files
import java.nio.file.Path

/**
 * Whether Rider's own unit-test gutter marks run the scenarios of a `.feature` file. The bundled backend module
 * adds them (its `FeatureTestGutterStage`), so the inline Run lens ([RunTestCodeVisionProvider]) stands down on
 * Scenario and Feature lines wherever the module is installed. Rule lines keep the lens: Rider has no test element
 * for a Rule, so there is no mark to run one with.
 *
 * The frontend cannot tell which scenarios the backend actually marked, so the presence of the module decides. If
 * a Rider release breaks the marks, the registry key [SHOW_RUN_LENS_KEY] brings the lens back.
 */
object BackendTestGutterMarks {
    /** Registry key (Help | Find Action | Registry) that shows the inline Run lens even when the backend marks scenarios. */
    const val SHOW_RUN_LENS_KEY = "reqnroll.feature.showRunLensWithGutterMarks"

    /** Path of the backend module inside the installed plugin, as `prepareSandbox` packages it. */
    internal const val BACKEND_ASSEMBLY = "dotnet/Reqnroll.IdeSupport.Rider.Backend.dll"

    /** Whether the backend module is part of the installed plugin. */
    val backendInstalled: Boolean by lazy {
        ReqnrollPlugin.descriptor()?.let { isBackendIn(it.pluginPath) } ?: false
    }

    /** Whether the Run lens should leave Scenario and Feature lines to the gutter marks. */
    val hideScenarioRunLens: Boolean
        get() = hideScenarioRunLens(backendInstalled, showRunLensRequested())

    internal fun hideScenarioRunLens(backendInstalled: Boolean, showRunLensRequested: Boolean): Boolean =
        backendInstalled && !showRunLensRequested

    internal fun isBackendIn(pluginPath: Path): Boolean = Files.isRegularFile(pluginPath.resolve(BACKEND_ASSEMBLY))

    private fun showRunLensRequested(): Boolean = try {
        Registry.`is`(SHOW_RUN_LENS_KEY, false)
    } catch (e: RuntimeException) {
        false
    }
}
