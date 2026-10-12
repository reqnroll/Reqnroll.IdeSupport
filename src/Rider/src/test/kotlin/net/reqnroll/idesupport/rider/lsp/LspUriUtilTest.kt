package net.reqnroll.idesupport.rider.lsp

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

    // localPathToLspUri (#909): must equal what Rider's LspServerDescriptor.getFileUri sends for didOpen.

    @Test
    fun `builds a three-slash uri for a unix path`() {
        assertEquals("file:///repo/Calculator.feature", localPathToLspUri("/repo/Calculator.feature"))
    }

    @Test
    fun `builds a three-slash lower-case-drive uri for a windows path`() {
        assertEquals(
            "file:///w:/Reqnroll/Calc/Addition.feature",
            localPathToLspUri("W:/Reqnroll/Calc/Addition.feature"),
        )
    }

    @Test
    fun `normalizes backslashes in a windows path`() {
        assertEquals("file:///c:/repo/F.feature", localPathToLspUri("C:\\repo\\F.feature"))
    }

    @Test
    fun `percent-encodes reserved characters`() {
        assertEquals("file:///repo/Price%20-%20Copy.feature", localPathToLspUri("/repo/Price - Copy.feature"))
        assertEquals("file:///c:/My%20Repo/F.feature", localPathToLspUri("C:/My Repo/F.feature"))
    }

    @Test
    fun `keeps a unc path's host`() {
        assertEquals("file://server/share/F.feature", localPathToLspUri("//server/share/F.feature"))
    }

    @Test
    fun `round-trips through lspUriToLocalPath for unix paths`() {
        val path = "/repo/Price - Copy.feature"
        assertEquals(path, lspUriToLocalPath(localPathToLspUri(path)))
    }
}
