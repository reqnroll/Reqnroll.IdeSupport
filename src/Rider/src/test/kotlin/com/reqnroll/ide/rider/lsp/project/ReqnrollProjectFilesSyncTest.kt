package com.reqnroll.ide.rider.lsp.project

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

// Paths are built with File.separator (not hardcoded backslashes) because findOwningProject
// matches on folder + File.separator, and this test runs on Linux CI runners as well as Windows.
class ReqnrollProjectFilesSyncTest {
    private val sut = ReqnrollProjectFilesSync()
    private val sep = File.separator

    @Test
    fun `findOwningProject matches a file under the project folder`() {
        val folder = "${sep}work${sep}App"
        val projectFile = "$folder${sep}App.csproj"
        val folders = listOf(folder to projectFile)

        assertEquals(projectFile, sut.findOwningProject("$folder${sep}Steps.cs", folders))
    }

    @Test
    fun `findOwningProject returns null when no folder covers the file`() {
        val folder = "${sep}work${sep}App"
        val folders = listOf(folder to "$folder${sep}App.csproj")

        assertNull(sut.findOwningProject("${sep}elsewhere${sep}Steps.cs", folders))
    }

    @Test
    fun `findOwningProject prefers the longest matching folder when nested`() {
        // Sorted longest-first, mirroring how ReqnrollProjectFilesSync.execute builds this list.
        val parent = "${sep}work${sep}Parent"
        val sub = "$parent${sep}Sub"
        val folders = listOf(
            sub to "$sub${sep}Sub.csproj",
            parent to "$parent${sep}Parent.csproj",
        )

        assertEquals(
            "$sub${sep}Sub.csproj",
            sut.findOwningProject("$sub${sep}Steps.cs", folders),
        )
    }

    @Test
    fun `findOwningProject matches case-insensitively (issue #328)`() {
        // Windows and (by default) macOS filesystems are case-insensitive; a path arriving with
        // different casing than the folder was registered with (e.g. via a symlink/junction)
        // must still match the same project rather than being dropped or misattributed.
        val folder = "${sep}work${sep}App"
        val projectFile = "$folder${sep}App.csproj"
        val folders = listOf(folder to projectFile)

        assertEquals(
            projectFile,
            sut.findOwningProject("${sep}WORK${sep}app${sep}Steps.cs", folders),
        )
    }

    @Test
    fun `findOwningProject does not let a sibling folder name that is a string-prefix of another collide`() {
        val fooBar = "${sep}work${sep}FooBar"
        val foo = "${sep}work${sep}Foo"
        val folders = listOf(
            fooBar to "$fooBar${sep}FooBar.csproj",
            foo to "$foo${sep}Foo.csproj",
        )

        assertEquals(
            "$fooBar${sep}FooBar.csproj",
            sut.findOwningProject("$fooBar${sep}Steps.cs", folders),
        )
    }
}
