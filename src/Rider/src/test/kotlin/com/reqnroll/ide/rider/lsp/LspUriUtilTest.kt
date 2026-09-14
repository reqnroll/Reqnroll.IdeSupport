package com.reqnroll.ide.rider.lsp

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class LspUriUtilTest {
    @Test
    fun `decodes a plain file uri with no special characters`() {
        assertEquals("/repo/Calculator.feature", lspUriToLocalPath("file:///repo/Calculator.feature"))
    }

    @Test
    fun `percent-decodes a space in the path`() {
        assertEquals(
            "/repo/Price - Copy.feature",
            lspUriToLocalPath("file:///repo/Price%20-%20Copy.feature"),
        )
    }

    @Test
    fun `percent-decodes other reserved characters`() {
        assertEquals("/repo/A#B.feature", lspUriToLocalPath("file:///repo/A%23B.feature"))
    }

    @Test
    fun `returns null for a non-file scheme`() {
        assertNull(lspUriToLocalPath("untitled:Untitled-1"))
    }

    @Test
    fun `returns null for a malformed uri`() {
        // A literal, un-encoded space is not a valid URI.
        assertNull(lspUriToLocalPath("file:///repo/has space.feature"))
    }

    @Test
    fun `returns null for a blank string`() {
        assertNull(lspUriToLocalPath(""))
    }
}
