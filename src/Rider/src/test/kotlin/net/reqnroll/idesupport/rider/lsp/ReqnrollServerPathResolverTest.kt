package net.reqnroll.idesupport.rider.lsp

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollServerPathResolverTest {
    @Test
    fun `rid selects win-arm64 for Windows on aarch64, win-x64 otherwise`() {
        assertEquals("win-x64", ReqnrollServerPathResolver.rid("Windows 11", "amd64"))
        assertEquals("win-arm64", ReqnrollServerPathResolver.rid("Windows 11", "aarch64"))
        assertEquals("win-arm64", ReqnrollServerPathResolver.rid("Windows 11", "arm64"))
    }

    @Test
    fun `rid selects osx-arm64 for Mac OS X with an arm-family arch`() {
        assertEquals("osx-arm64", ReqnrollServerPathResolver.rid("Mac OS X", "aarch64"))
        assertEquals("osx-arm64", ReqnrollServerPathResolver.rid("Mac OS X", "arm"))
    }

    @Test
    fun `rid selects osx-x64 for Mac OS X with a non-arm arch`() {
        assertEquals("osx-x64", ReqnrollServerPathResolver.rid("Mac OS X", "x86_64"))
    }

    @Test
    fun `rid selects linux-arm64 for Linux with an arm64 arch`() {
        assertEquals("linux-arm64", ReqnrollServerPathResolver.rid("Linux", "aarch64"))
        assertEquals("linux-arm64", ReqnrollServerPathResolver.rid("Linux", "arm64"))
    }

    @Test
    fun `rid selects linux-x64 for Linux amd64`() {
        assertEquals("linux-x64", ReqnrollServerPathResolver.rid("Linux", "amd64"))
    }

    @Test
    fun `rid falls back to linux-x64 for anything else`() {
        assertEquals("linux-x64", ReqnrollServerPathResolver.rid("FreeBSD", "amd64"))
    }

    @Test
    fun `isWindows is case-insensitive`() {
        assertTrue(ReqnrollServerPathResolver.isWindows("Windows 11"))
        assertTrue(ReqnrollServerPathResolver.isWindows("WINDOWS 10"))
        assertFalse(ReqnrollServerPathResolver.isWindows("Mac OS X"))
        assertFalse(ReqnrollServerPathResolver.isWindows("Linux"))
    }

    @Test
    fun `binaryName appends exe only on Windows`() {
        assertEquals("Reqnroll.IdeSupport.LSP.Server.exe", ReqnrollServerPathResolver.binaryName("Windows 11"))
        assertEquals("Reqnroll.IdeSupport.LSP.Server", ReqnrollServerPathResolver.binaryName("Mac OS X"))
        assertEquals("Reqnroll.IdeSupport.LSP.Server", ReqnrollServerPathResolver.binaryName("Linux"))
    }
}
