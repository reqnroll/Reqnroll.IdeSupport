package com.reqnroll.ide.rider.lsp.project

import com.reqnroll.ide.rider.lsp.protocol.ProjectFileRole
import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

// Issue #736. The .projitems/.csproj content below uses backslashes on purpose — that is how
// Visual Studio writes them — so these also prove the separator normalization on Linux runners.
class SharedProjectItemsTest {
    private val root: File = Files.createTempDirectory("reqnroll-736-").toFile()

    @AfterTest
    fun cleanUp() {
        root.deleteRecursively()
    }

    @Test
    fun `returns the items of an imported projitems as full paths`() {
        val feature = touch("Shared/Features/Calculator.feature")
        val steps = touch("Shared/Steps/CalculatorSteps.cs")
        writeProjItems(
            "Shared/Shared.projitems",
            """<Compile Include="${'$'}(MSBuildThisFileDirectory)Steps\CalculatorSteps.cs" />""",
            """<None Include="${'$'}(MSBuildThisFileDirectory)Features\Calculator.feature" />""",
        )
        val project = writeProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" Label="Shared" />""")

        assertEquals(listOf(steps, feature), SharedProjectItems.getImportedFiles(project))
    }

    @Test
    fun `returns nothing for a project with no shared import`() {
        touch("Tests/Other.props")
        val project = writeProject("Tests/Tests.csproj", """<Import Project="Other.props" />""")

        assertTrue(SharedProjectItems.getImportedFiles(project).isEmpty())
    }

    @Test
    fun `returns nothing when the project file is missing or malformed`() {
        assertTrue(SharedProjectItems.getImportedFiles(File(root, "Missing.csproj").path).isEmpty())

        val malformed = File(root, "Malformed.csproj").apply { writeText("<Project><Import") }
        assertTrue(SharedProjectItems.getImportedFiles(malformed.path).isEmpty())
    }

    @Test
    fun `skips paths naming a property it cannot resolve`() {
        touch("Shared/Steps.cs")
        writeProjItems("Shared/Shared.projitems", """<Compile Include="${'$'}(SomeOtherRoot)Steps.cs" />""")
        val project = writeProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""")

        assertTrue(SharedProjectItems.getImportedFiles(project).isEmpty())
    }

    @Test
    fun `ignores item types that do not carry sources and items that do not exist`() {
        val steps = touch("Shared/Steps.cs")
        touch("Shared/Strings.resx")
        writeProjItems(
            "Shared/Shared.projitems",
            """<EmbeddedResource Include="${'$'}(MSBuildThisFileDirectory)Strings.resx" />""",
            """<Compile Include="${'$'}(MSBuildThisFileDirectory)Steps.cs" />""",
            """<Compile Include="${'$'}(MSBuildThisFileDirectory)Deleted.cs" />""",
        )
        val project = writeProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""")

        assertEquals(listOf(steps), SharedProjectItems.getImportedFiles(project))
    }

    @Test
    fun `expands wildcards and honours Exclude and Remove`() {
        val a = touch("Shared/Features/A.feature")
        val b = touch("Shared/Features/Nested/B.feature")
        touch("Shared/Features/Draft.feature")
        touch("Shared/Features/Nested/Removed.feature")
        writeProjItems(
            "Shared/Shared.projitems",
            """<None Include="${'$'}(MSBuildThisFileDirectory)**\*.feature" Exclude="${'$'}(MSBuildThisFileDirectory)Features\Draft.feature" />""",
            """<None Remove="${'$'}(MSBuildThisFileDirectory)Features\Nested\Removed.feature" />""",
        )
        val project = writeProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""")

        assertEquals(setOf(a, b), SharedProjectItems.getImportedFiles(project).toSet())
    }

    @Test
    fun `baseline includes shared-project files alongside the project folder's own`() {
        val own = touch("Tests/OwnSteps.cs")
        val sharedFeature = touch("Shared/Calculator.feature")
        writeProjItems("Shared/Shared.projitems", """<None Include="${'$'}(MSBuildThisFileDirectory)Calculator.feature" />""")
        val project = writeProject("Tests/Tests.csproj", """<Import Project="..\Shared\Shared.projitems" />""")

        val entries = ReqnrollProjectBaseline.buildProjectFileEntries(project).orEmpty()

        assertEquals(
            setOf(own to ProjectFileRole.BINDING, sharedFeature to ProjectFileRole.FEATURE),
            entries.map { it.path to it.role }.toSet(),
        )
    }

    @Test
    fun `baseline lists a shared file inside the project folder once`() {
        val nested = touch("Tests/Shared/Steps.cs")
        writeProjItems("Tests/Shared/Shared.projitems", """<Compile Include="${'$'}(MSBuildThisFileDirectory)STEPS.cs" />""")
        val project = writeProject("Tests/Tests.csproj", """<Import Project="Shared\Shared.projitems" />""")

        val entries = ReqnrollProjectBaseline.buildProjectFileEntries(project).orEmpty()

        // The .projitems spells the file differently only on case-insensitive filesystems, where
        // it resolves to the same file; on Linux STEPS.cs does not exist and is skipped.
        assertEquals(listOf(nested), entries.map { it.path })
    }

    private fun touch(relativePath: String): String {
        val file = File(root, relativePath)
        file.parentFile.mkdirs()
        file.writeText("")
        return file.path
    }

    @Test
    fun `baseline skips the project's own bin and obj but not a nested bin folder`() {
        val steps = touch("Tests/Steps.cs")
        val nestedBin = touch("Tests/Features/bin/Deep.feature")
        touch("Tests/obj/Debug/net8.0/Tests.AssemblyInfo.cs")
        touch("Tests/obj/Debug/net8.0/Tests.GlobalUsings.g.cs")
        touch("Tests/bin/Debug/net8.0/Leftover.cs")
        val project = writeProject("Tests/Tests.csproj")

        val entries = ReqnrollProjectBaseline.buildProjectFileEntries(project).orEmpty()

        assertEquals(setOf(steps, nestedBin), entries.map { it.path }.toSet())
    }

    // Imports/items are substituted *after* trimIndent(): interpolating several lines directly
    // leaves the extra lines at column 0, so trimIndent() strips nothing and the <?xml?>
    // declaration keeps its leading whitespace -- which the XML parser rejects.
    private fun writeProject(relativePath: String, vararg imports: String): String {
        val file = File(root, relativePath)
        file.parentFile.mkdirs()
        file.writeText(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              IMPORTS
            </Project>
            """.trimIndent().replace("IMPORTS", imports.joinToString("\n")),
        )
        return file.path
    }

    private fun writeProjItems(relativePath: String, vararg items: String) {
        val file = File(root, relativePath)
        file.parentFile.mkdirs()
        file.writeText(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><HasSharedItems>true</HasSharedItems></PropertyGroup>
              <ItemGroup>
                ITEMS
              </ItemGroup>
            </Project>
            """.trimIndent().replace("ITEMS", items.joinToString("\n")),
        )
    }
}
