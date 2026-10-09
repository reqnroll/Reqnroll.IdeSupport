package net.reqnroll.idesupport.rider.structureview

import com.intellij.icons.AllIcons
import org.eclipse.lsp4j.SymbolKind
import kotlin.test.Test
import kotlin.test.assertEquals

class ReqnrollSymbolTreeElementTest {
    @Test
    fun `iconFor maps Feature (Module) to the module icon`() {
        assertEquals(AllIcons.Nodes.Module, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Module))
    }

    @Test
    fun `iconFor maps Background (Constructor) to the method icon`() {
        assertEquals(AllIcons.Nodes.Method, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Constructor))
    }

    @Test
    fun `iconFor maps Rule (Namespace) to the package icon`() {
        assertEquals(AllIcons.Nodes.Package, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Namespace))
    }

    @Test
    fun `iconFor maps Scenario (Method) to the method icon`() {
        assertEquals(AllIcons.Nodes.Method, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Method))
    }

    @Test
    fun `iconFor maps Step (Field) to the field icon`() {
        assertEquals(AllIcons.Nodes.Field, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Field))
    }

    @Test
    fun `iconFor maps Examples (Array) to the parameter icon`() {
        assertEquals(AllIcons.Nodes.Parameter, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Array))
    }

    @Test
    fun `iconFor falls back to the unknown icon for unmapped kinds`() {
        assertEquals(AllIcons.Nodes.Unknown, ReqnrollSymbolTreeElement.iconFor(SymbolKind.Class))
    }
}
