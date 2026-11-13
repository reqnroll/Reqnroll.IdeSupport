package net.reqnroll.idesupport.rider.lsp.project

import com.intellij.openapi.progress.ProcessCanceledException
import com.jetbrains.rider.model.RdTargetFrameworkId
import com.jetbrains.rider.model.RdVersionInfo
import java.io.File
import java.nio.file.Files
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ReqnrollProjectBaselineTest {
    @Test
    fun `toClassicMoniker builds the dotted NETCoreApp moniker from version fields`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(9, 0, 0), "net9.0", ".NET 9.0", true, false,
        )

        assertEquals(".NETCoreApp,Version=v9.0", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    @Test
    fun `toClassicMoniker builds the dotted NETFramework moniker from version fields`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(4, 6, 2), "net462", ".NET Framework 4.6.2", false, true,
        )

        assertEquals(".NETFramework,Version=v4.6.2", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    @Test
    fun `toClassicMoniker falls back to shortName when neither flag is set`() {
        val tfm = RdTargetFrameworkId(
            RdVersionInfo(0, 0, 0), "netstandard2.0", ".NET Standard 2.0", false, false,
        )

        assertEquals("netstandard2.0", ReqnrollProjectBaseline.toClassicMoniker(tfm))
    }

    private val sep = File.separator
    private val folder = "${sep}work${sep}App"

    @Test
    fun `isBuildOutput matches the project-root bin and obj folders and their contents`() {
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}obj", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}bin", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}obj${sep}Debug${sep}net8.0${sep}App.AssemblyInfo.cs", folder))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}OBJ${sep}x.cs", folder))
    }

    @Test
    fun `isBuildOutput accepts slash event paths against a backslash Windows folder (issue 978)`() {
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("C:/proj/obj/Debug/x.cs", "C:\\proj"))
        assertTrue(ReqnrollProjectBaseline.isBuildOutput("C:/proj/bin", "C:\\proj"))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("C:/proj/Steps.cs", "C:\\proj"))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("C:/proj/objects/A.cs", "C:\\proj"))
    }

    @Test
    fun `isBuildOutput ignores nested bin folders and look-alike names`() {
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}Features${sep}bin${sep}A.feature", folder))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}objects${sep}A.cs", folder))
        assertFalse(ReqnrollProjectBaseline.isBuildOutput("$folder${sep}Steps.cs", folder))
    }

    @Test
    fun `isSkippedFolder matches node_modules and git folders case-insensitively`() {
        assertTrue(ReqnrollProjectBaseline.isSkippedFolder("node_modules"))
        assertTrue(ReqnrollProjectBaseline.isSkippedFolder("Node_Modules"))
        assertTrue(ReqnrollProjectBaseline.isSkippedFolder(".git"))
        assertFalse(ReqnrollProjectBaseline.isSkippedFolder("Features"))
        assertFalse(ReqnrollProjectBaseline.isSkippedFolder("git"))
        assertFalse(ReqnrollProjectBaseline.isSkippedFolder("node_modules_backup"))
    }

    @Test
    fun `buildProjectFileEntries does not descend into node_modules or git folders at any depth`() {
        val root = Files.createTempDirectory("reqnroll-993-").toFile()
        try {
            fun write(relative: String) = File(root, relative).apply { parentFile.mkdirs(); writeText("") }
            val projectFile = write("App.csproj").path
            val kept = listOf(write("Steps.cs"), write("Features${sep}A.feature"))
            write("node_modules${sep}pkg${sep}Index.cs")
            write(".git${sep}hooks${sep}Hook.cs")
            write("Features${sep}node_modules${sep}B.feature")

            val entries = ReqnrollProjectBaseline.buildProjectFileEntries(projectFile)!!

            assertEquals(kept.map { it.path }.sorted(), entries.map { it.path }.sorted())
        } finally {
            root.deleteRecursively()
        }
    }

    @Test
    fun `sendEachIsolated keeps sending the remaining items after one throws`() {
        val sent = mutableListOf<String>()

        ReqnrollProjectBaseline.sendEachIsolated(
            listOf("A", "B", "C"),
            isCancelled = { false },
            describe = { it },
        ) { item ->
            if (item == "B") throw java.io.UncheckedIOException(java.io.IOException("unreadable"))
            sent += item
        }

        assertEquals(listOf("A", "C"), sent)
    }

    @Test
    fun `sendEachIsolated stops once cancelled`() {
        val sent = mutableListOf<String>()

        ReqnrollProjectBaseline.sendEachIsolated(
            listOf("A", "B", "C"),
            isCancelled = { sent.size >= 1 },
            describe = { it },
        ) { sent += it }

        assertEquals(listOf("A"), sent)
    }

    @Test
    fun `sendEachIsolated rethrows platform cancellation instead of continuing`() {
        val sent = mutableListOf<String>()

        assertFailsWith<ProcessCanceledException> {
            ReqnrollProjectBaseline.sendEachIsolated(
                listOf("A", "B"),
                isCancelled = { false },
                describe = { it },
            ) { item ->
                if (item == "A") throw ProcessCanceledException()
                sent += item
            }
        }

        assertEquals(emptyList(), sent)
    }
}
