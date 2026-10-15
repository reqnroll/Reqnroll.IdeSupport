package net.reqnroll.idesupport.rider.breadcrumbs

import com.intellij.icons.AllIcons
import org.eclipse.lsp4j.SymbolKind
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollSymbolIconsTest {
    @Test
    fun `iconFor maps Feature (Module) to the module icon`() {
        assertEquals(AllIcons.Nodes.Module, ReqnrollSymbolIcons.iconFor(SymbolKind.Module))
    }

    @Test
    fun `iconFor maps Background (Constructor) to the method icon`() {
        assertEquals(AllIcons.Nodes.Method, ReqnrollSymbolIcons.iconFor(SymbolKind.Constructor))
    }

    @Test
    fun `iconFor maps Rule (Namespace) to the package icon`() {
        assertEquals(AllIcons.Nodes.Package, ReqnrollSymbolIcons.iconFor(SymbolKind.Namespace))
    }

    @Test
    fun `iconFor maps Scenario (Method) to the method icon`() {
        assertEquals(AllIcons.Nodes.Method, ReqnrollSymbolIcons.iconFor(SymbolKind.Method))
    }

    @Test
    fun `iconFor maps Step (Field) to the field icon`() {
        assertEquals(AllIcons.Nodes.Field, ReqnrollSymbolIcons.iconFor(SymbolKind.Field))
    }

    @Test
    fun `iconFor maps Examples (Array) to the parameter icon`() {
        assertEquals(AllIcons.Nodes.Parameter, ReqnrollSymbolIcons.iconFor(SymbolKind.Array))
    }

    @Test
    fun `iconFor falls back to the unknown icon for unmapped kinds`() {
        assertEquals(AllIcons.Nodes.Unknown, ReqnrollSymbolIcons.iconFor(SymbolKind.Class))
    }
}
