package com.reqnroll.ide.rider.lsp.project

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ReqnrollProjectFilesSyncTest {
    private val sut = ReqnrollProjectFilesSync()

    @Test
    fun `findOwningProject matches a file under the project folder`() {
        val folders = listOf("C:\\work\\App" to "C:\\work\\App\\App.csproj")

        assertEquals(
            "C:\\work\\App\\App.csproj",
            sut.findOwningProject("C:\\work\\App\\Steps.cs", folders),
        )
    }

    @Test
    fun `findOwningProject returns null when no folder covers the file`() {
        val folders = listOf("C:\\work\\App" to "C:\\work\\App\\App.csproj")

        assertNull(sut.findOwningProject("C:\\elsewhere\\Steps.cs", folders))
    }

    @Test
    fun `findOwningProject prefers the longest matching folder when nested`() {
        // Sorted longest-first, mirroring how ReqnrollProjectFilesSync.execute builds this list.
        val folders = listOf(
            "C:\\work\\Parent\\Sub" to "C:\\work\\Parent\\Sub\\Sub.csproj",
            "C:\\work\\Parent" to "C:\\work\\Parent\\Parent.csproj",
        )

        assertEquals(
            "C:\\work\\Parent\\Sub\\Sub.csproj",
            sut.findOwningProject("C:\\work\\Parent\\Sub\\Steps.cs", folders),
        )
    }

    @Test
    fun `findOwningProject matches case-insensitively (issue #328)`() {
        // Windows and (by default) macOS filesystems are case-insensitive; a path arriving with
        // different casing than the folder was registered with (e.g. via a symlink/junction)
        // must still match the same project rather than being dropped or misattributed.
        val folders = listOf("C:\\work\\App" to "C:\\work\\App\\App.csproj")

        assertEquals(
            "C:\\work\\App\\App.csproj",
            sut.findOwningProject("c:\\WORK\\app\\Steps.cs", folders),
        )
    }

    @Test
    fun `findOwningProject does not let a sibling folder name that is a string-prefix of another collide`() {
        val folders = listOf(
            "C:\\work\\FooBar" to "C:\\work\\FooBar\\FooBar.csproj",
            "C:\\work\\Foo" to "C:\\work\\Foo\\Foo.csproj",
        )

        assertEquals(
            "C:\\work\\FooBar\\FooBar.csproj",
            sut.findOwningProject("C:\\work\\FooBar\\Steps.cs", folders),
        )
    }
}
