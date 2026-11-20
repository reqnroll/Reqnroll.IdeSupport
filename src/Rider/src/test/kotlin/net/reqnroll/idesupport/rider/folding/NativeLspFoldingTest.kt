package net.reqnroll.idesupport.rider.folding

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class NativeLspFoldingTest {
    private val loader = javaClass.classLoader

    @Test
    fun `a class the loader can see is reported as present`() {
        assertTrue(NativeLspFolding.classExists("java.lang.String", loader))
        assertTrue(NativeLspFolding.classExists("com.intellij.platform.lsp.api.LspServerDescriptor", loader))
    }

    @Test
    fun `a class that does not exist is reported as absent`() {
        assertFalse(NativeLspFolding.classExists("com.intellij.platform.lsp.api.customization.NoSuchFeatureSupport", loader))
    }

    @Test
    fun `the Rider the plugin is compiled against has no native folding, so the controller runs`() {
        assertFalse(NativeLspFolding.isAvailable)
    }
}
