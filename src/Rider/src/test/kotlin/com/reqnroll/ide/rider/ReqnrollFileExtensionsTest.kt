package com.reqnroll.ide.rider

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollFileExtensionsTest {
    @Test
    fun `isFeatureExtension matches regardless of casing`() {
        assertTrue(isFeatureExtension("feature"))
        assertTrue(isFeatureExtension("Feature"))
        assertTrue(isFeatureExtension("FEATURE"))
    }

    @Test
    fun `isFeatureExtension rejects other extensions and null`() {
        assertFalse(isFeatureExtension("cs"))
        assertFalse(isFeatureExtension(null))
        assertFalse(isFeatureExtension(""))
    }

    @Test
    fun `isCsExtension matches regardless of casing`() {
        assertTrue(isCsExtension("cs"))
        assertTrue(isCsExtension("Cs"))
        assertTrue(isCsExtension("CS"))
    }

    @Test
    fun `isCsExtension rejects other extensions and null`() {
        assertFalse(isCsExtension("feature"))
        assertFalse(isCsExtension(null))
        assertFalse(isCsExtension(""))
    }
}
