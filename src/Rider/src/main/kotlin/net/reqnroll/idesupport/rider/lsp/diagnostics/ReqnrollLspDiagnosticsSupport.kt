package net.reqnroll.idesupport.rider.lsp.diagnostics

import com.intellij.platform.lsp.api.customization.LspDiagnosticsSupport
import com.intellij.xml.util.XmlStringUtil
import org.eclipse.lsp4j.Diagnostic

/**
 * Renders multi-line diagnostic messages (e.g. the ambiguous-step "Ambiguous steps:\n<candidate1>\n
 * <candidate2>..." text built by `MatchResult.CreateMultiMatch`) as real line breaks in the hover
 * tooltip. The platform default (`LspDiagnosticsSupport.getTooltip`) forwards `Diagnostic.message`
 * verbatim into `AnnotationBuilder.tooltip(String)`; that string is rendered as HTML by Rider's hover
 * popup, and bare `\n` characters collapse like any other whitespace run in HTML, merging every
 * candidate onto one row instead of one per line — confirmed by decompiling
 * `LspDiagnosticsSupport.getTooltip` in `product.jar` (pinned Rider 2024.3.5), which shows it does
 * `return diagnostic.message` with no HTML conversion at all. `XmlStringUtil.wrapInHtmlLines` builds
 * `<html><nobr>line1</nobr><br><nobr>line2</nobr>...</html>`, giving each line its own row regardless
 * of client whitespace handling. See #176.
 */
class ReqnrollLspDiagnosticsSupport : LspDiagnosticsSupport() {
    override fun getTooltip(diagnostic: Diagnostic): String {
        val lines = diagnostic.message
            .split(Regex("\r\n|\r|\n"))
            .map { XmlStringUtil.escapeString(it) as CharSequence }
            .toTypedArray()
        return XmlStringUtil.wrapInHtmlLines(*lines)
    }
}
