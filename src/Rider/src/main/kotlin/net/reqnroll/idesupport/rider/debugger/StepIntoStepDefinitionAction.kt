package net.reqnroll.idesupport.rider.debugger

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataContext
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.MessageType
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XDebuggerUtil
import com.intellij.xdebugger.XSourcePosition
import net.reqnroll.idesupport.rider.isFeatureExtension
import net.reqnroll.idesupport.rider.logging.ReqnrollDebugLogger
import net.reqnroll.idesupport.rider.lsp.ReqnrollRequestSender
import net.reqnroll.idesupport.rider.lsp.localPathToLspUri
import net.reqnroll.idesupport.rider.lsp.lspUriToLocalPath
import org.eclipse.lsp4j.Location

/**
 * Step Into for a debugger stopped on a `.feature` step line: runs to the first line of the step definition the
 * step binds to.
 *
 * Rider's own Step Into cannot get there. The step's generated code awaits `testRunner.GivenAsync(...)`, and the
 * step definition is reached through Reqnroll's asynchronous execution engine. With external source debugging off,
 * Rider steps over the whole call to the next step; with it on, it steps into Reqnroll's `ExecutionEngine`. (Visual
 * Studio's Just My Code stepping lands in the step definition.)
 *
 * This action asks the server where the step's definition is (the same `textDocument/definition` lookup as Go to
 * Definition) and runs to the start of that method's body, as Run to Cursor does. Breakpoints on the way (in hooks,
 * say) still stop the run. When the step has no single definition with source on this machine (undefined,
 * ambiguous, or bound to an external assembly), it falls back to the platform's Step Into.
 *
 * It shares Step Into's shortcuts; [StepIntoStepDefinitionPromoter] makes it win them only while the debugger is
 * stopped on a `.feature` line, so everywhere else the shortcut keeps its normal meaning.
 */
class StepIntoStepDefinitionAction : AnAction() {
    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val session = sessionOf(e.dataContext)
        e.presentation.isVisible = session != null
        e.presentation.isEnabled = session != null && isStoppedOnFeatureLine(session)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val session = sessionOf(e.dataContext) ?: return
        val position = session.currentPosition ?: return
        if (!isStoppedOnFeatureLine(session)) {
            session.stepInto()
            return
        }
        val suspendContext = session.suspendContext
        val featureFile = position.file
        val featureLine = position.line
        // actionPerformed runs on the EDT, where reading a document needs no read action.
        val character = FileDocumentManager.getInstance().getDocument(featureFile)
            ?.takeIf { featureLine in 0 until it.lineCount }
            ?.let { firstNonBlankCharacter(it.charsSequence, it.getLineStartOffset(featureLine), it.getLineEndOffset(featureLine)) }
            ?: 0
        val uri = localPathToLspUri(featureFile.path)

        ApplicationManager.getApplication().executeOnPooledThread {
            val lookup = lookUpDefinition(project, uri, featureLine, character)
            ApplicationManager.getApplication().invokeLater {
                // The user may have resumed or stepped while the server was answering.
                if (session.isStopped || !session.isSuspended || session.suspendContext !== suspendContext) return@invokeLater
                val target = when (lookup) {
                    is Lookup.Found -> targetOf(lookup.location)
                    is Lookup.Failed -> Target.Fallback(lookup.reason)
                }
                when (target) {
                    is Target.RunTo -> {
                        ReqnrollDebugLogger.info(
                            "StepIntoStepDefinitionAction: running to ${target.position.file.path}:${target.position.line + 1}")
                        session.runToPosition(target.position, false)
                    }
                    is Target.Fallback -> {
                        ReqnrollDebugLogger.info("StepIntoStepDefinitionAction: ${target.reason}; using Step Into")
                        session.reportMessage("${target.reason}; using Step Into.", MessageType.INFO)
                        session.stepInto()
                    }
                }
            }
        }
    }

    internal sealed interface Target {
        data class RunTo(val position: XSourcePosition) : Target
        data class Fallback(val reason: String) : Target
    }

    private sealed interface Lookup {
        data class Found(val location: Location) : Lookup
        data class Failed(val reason: String) : Lookup
    }

    /** Asks the server for the step's definition. Blocks; call off the EDT. */
    private fun lookUpDefinition(project: Project, uri: String, line: Int, character: Int): Lookup {
        val locations = ReqnrollRequestSender.definition(project, uri, line, character)
            ?: return Lookup.Failed("The Reqnroll server did not answer")
        return when (val choice = chooseLocation(locations)) {
            is LocationChoice.Single -> Lookup.Found(choice.location)
            LocationChoice.None -> Lookup.Failed("No step definition with source found for this step")
            is LocationChoice.Several -> Lookup.Failed("This step matches ${choice.count} step definitions")
        }
    }

    /** Where to run to for the step definition at [location]. Runs on the EDT, which may read documents. */
    private fun targetOf(location: Location): Target {
        val path = lspUriToLocalPath(location.uri) ?: return Target.Fallback("Cannot open ${location.uri}")
        val file = LocalFileSystem.getInstance().findFileByPath(path) ?: return Target.Fallback("Cannot find $path")
        val document = FileDocumentManager.getInstance().getDocument(file)
            ?: return Target.Fallback("Cannot read ${file.name}")
        val startLine = location.range.start.line
        val bodyLine = if (startLine in 0 until document.lineCount) {
            val nameOffset = (document.getLineStartOffset(startLine) + location.range.start.character)
                .coerceAtMost(document.getLineEndOffset(startLine))
            StepDefinitionBodyLocator.bodyStartLine(document.charsSequence, nameOffset)
        } else {
            null
        } ?: return Target.Fallback("Cannot find the body of the step definition in ${file.name}")
        val position = XDebuggerUtil.getInstance().createPosition(file, bodyLine)
            ?: return Target.Fallback("Cannot run to ${file.name}:${bodyLine + 1}")
        return Target.RunTo(position)
    }

    internal sealed interface LocationChoice {
        data class Single(val location: Location) : LocationChoice
        data object None : LocationChoice
        data class Several(val count: Int) : LocationChoice
    }

    companion object {
        /** The session the action applies to: the one in the context, else the project's current one. */
        internal fun sessionOf(context: DataContext): XDebugSession? =
            XDebugSession.DATA_KEY.getData(context)
                ?: CommonDataKeys.PROJECT.getData(context)?.let { XDebuggerManager.getInstance(it).currentSession }

        internal fun isStoppedOnFeatureLine(session: XDebugSession): Boolean =
            !session.isStopped && session.isSuspended && isFeatureExtension(session.currentPosition?.file?.extension)

        /** Duplicate locations (the same method reported twice) count once. */
        internal fun chooseLocation(locations: List<Location>): LocationChoice {
            val distinct = locations.distinctBy { Triple(it.uri, it.range.start.line, it.range.start.character) }
            return when (distinct.size) {
                0 -> LocationChoice.None
                1 -> LocationChoice.Single(distinct[0])
                else -> LocationChoice.Several(distinct.size)
            }
        }

        /** The character of the step's first non-blank character, so the server's lookup lands inside the step. */
        internal fun firstNonBlankCharacter(text: CharSequence, lineStart: Int, lineEnd: Int): Int {
            var i = lineStart
            while (i < lineEnd && text[i].isWhitespace()) i++
            return i - lineStart
        }
    }
}
