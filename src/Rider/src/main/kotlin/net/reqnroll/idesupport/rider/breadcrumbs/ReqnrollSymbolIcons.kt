package net.reqnroll.idesupport.rider.breadcrumbs

import com.intellij.icons.AllIcons
import org.eclipse.lsp4j.SymbolKind
import javax.swing.Icon

internal object ReqnrollSymbolIcons {
    /** Mirrors `FeatureDocumentSymbolHandler.ToSymbolKind`'s Feature/Rule/Scenario/Step mapping. `internal` so it's unit-testable without a platform fixture. */
    fun iconFor(kind: SymbolKind): Icon = when (kind) {
        SymbolKind.Module -> AllIcons.Nodes.Module // Feature
        SymbolKind.Constructor -> AllIcons.Nodes.Method // Background
        SymbolKind.Namespace -> AllIcons.Nodes.Package // Rule
        SymbolKind.Method -> AllIcons.Nodes.Method // Scenario / Scenario Outline
        SymbolKind.Field -> AllIcons.Nodes.Field // Step
        SymbolKind.Array -> AllIcons.Nodes.Parameter // Examples
        else -> AllIcons.Nodes.Unknown
    }
}
