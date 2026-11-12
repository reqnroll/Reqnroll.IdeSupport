package net.reqnroll.idesupport.rider.codevision

import kotlin.test.Test
import kotlin.test.assertEquals

class EditorLensRefreshTest {
    @Test
    fun `adds the platform lens provider where lenses are native`() {
        assertEquals(
            listOf("Reqnroll.RunTestCodeVision", "LspCodeVisionProvider"),
            EditorLensRefresh.withPlatformProvider(listOf("Reqnroll.RunTestCodeVision"), nativeLenses = true),
        )
    }

    @Test
    fun `leaves the list alone where lenses are not native`() {
        assertEquals(
            listOf("Reqnroll.RunTestCodeVision"),
            EditorLensRefresh.withPlatformProvider(listOf("Reqnroll.RunTestCodeVision"), nativeLenses = false),
        )
    }

    @Test
    fun `does not add the platform provider twice`() {
        assertEquals(
            listOf("LspCodeVisionProvider"),
            EditorLensRefresh.withPlatformProvider(listOf("LspCodeVisionProvider"), nativeLenses = true),
        )
    }
}
