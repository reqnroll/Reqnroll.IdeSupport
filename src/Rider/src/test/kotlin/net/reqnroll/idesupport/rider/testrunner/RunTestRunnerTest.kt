package net.reqnroll.idesupport.rider.testrunner

import net.reqnroll.idesupport.rider.lsp.protocol.ScenarioTestTargetItem
import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class RunTestRunnerTest {
    private val tempDirs = mutableListOf<File>()

    @AfterTest
    fun cleanUpTempDirs() {
        tempDirs.forEach { it.deleteRecursively() }
    }

    private fun tempDir(): File {
        val dir = Files.createTempDirectory("reqnroll-run-test-runner-test").toFile()
        tempDirs += dir
        return dir
    }
    private fun target(
        declaringTypeFullName: String = "Tests.FFeature",
        methodName: String = "AddNumbers",
    ) = ScenarioTestTargetItem(declaringTypeFullName = declaringTypeFullName, methodName = methodName)

    // ── findOwningProjectPath ────────────────────────────────────────────────

    @Test
    fun `finds the project whose folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj", "/repo/Bar/Bar.csproj")
        assertEquals("/repo/Foo/Foo.csproj", RunTestRunner.findOwningProjectPath("/repo/Foo/Features/A.feature", projects))
    }

    @Test
    fun `deepest matching folder wins for a nested project`() {
        val projects = listOf("/repo/Foo.csproj", "/repo/Foo/Nested/Nested.csproj")
        assertEquals(
            "/repo/Foo/Nested/Nested.csproj",
            RunTestRunner.findOwningProjectPath("/repo/Foo/Nested/A.feature", projects),
        )
    }

    @Test
    fun `finds the owner of a forward-slash file path on a non-C Windows drive from backslash project paths`() {
        // lspUriToLocalPath yields "W:/..." while the project model reports "W:\..." on Windows.
        val projects = listOf(
            "W:\\Reqnroll\\Reqnroll.ExploratoryTestProjects\\ReqnrollCalculator\\ReqnrollCalculator.Specs\\ReqnrollCalculator.Specs.csproj",
        )
        assertEquals(
            projects[0],
            RunTestRunner.findOwningProjectPath(
                "W:/Reqnroll/Reqnroll.ExploratoryTestProjects/ReqnrollCalculator/ReqnrollCalculator.Specs/Features/Addition.feature",
                projects,
            ),
        )
    }

    @Test
    fun `matches across drive-letter case and separator style, deepest folder still wins`() {
        val projects = listOf("w:\\repo\\Foo.csproj", "W:\\repo\\Foo\\Nested\\Nested.csproj")
        assertEquals(projects[1], RunTestRunner.findOwningProjectPath("W:/repo/Foo/Nested/A.feature", projects))
        assertEquals(projects[0], RunTestRunner.findOwningProjectPath("W:/repo/Other/A.feature", projects))
    }

    @Test
    fun `a sibling folder sharing a name prefix does not own the file`() {
        val projects = listOf("W:\\repo\\Foo\\Foo.csproj")
        assertNull(RunTestRunner.findOwningProjectPath("W:/repo/FooBar/A.feature", projects))
    }

    @Test
    fun `matches a forward-slashed project path against a backslashed file path`() {
        val projects = listOf("C:/proj/Proj.csproj")
        assertEquals("C:/proj/Proj.csproj", RunTestRunner.findOwningProjectPath("""C:\proj\F.feature""", projects))
    }

    @Test
    fun `matches a lower-case file path against a mixed-case Windows project path`() {
        val projects = listOf("""C:\Proj\Proj.csproj""")
        assertEquals("""C:\Proj\Proj.csproj""", RunTestRunner.findOwningProjectPath("c:/proj/F.feature", projects))
    }

    @Test
    fun `returns null when a Windows file is outside every project folder`() {
        val projects = listOf("""C:\proj\Proj.csproj""")
        assertNull(RunTestRunner.findOwningProjectPath("D:/other/F.feature", projects))
    }

    @Test
    fun `returns null when no project folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj")
        assertNull(RunTestRunner.findOwningProjectPath("/elsewhere/A.feature", projects))
    }

    @Test
    fun `returns null for an empty project list`() {
        assertNull(RunTestRunner.findOwningProjectPath("/repo/Foo/A.feature", emptyList()))
    }

    // ── normalizeToManagedAssemblyPath ───────────────────────────────────────

    @Test
    fun `normalizeToManagedAssemblyPath returns a dll path unchanged`() {
        val path = "/repo/bin/Debug/net8.0/Tests.dll"
        assertEquals(path, RunTestRunner.normalizeToManagedAssemblyPath(path))
    }

    @Test
    fun `normalizeToManagedAssemblyPath resolves an apphost binary to its dll sibling when it exists`() {
        // Regression guard: an MTP-mode project's exePath points at the native
        // apphost (no extension on Linux/macOS), not the managed .dll the reporter actually reports.
        val root = tempDir()
        val exePath = File(root, "Tests").path
        File(root, "Tests.dll").writeText("not a real assembly, just needs to exist")

        assertEquals(File(root, "Tests.dll").path, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    @Test
    fun `normalizeToManagedAssemblyPath resolves a Windows apphost exe to its dll sibling`() {
        val root = tempDir()
        val exePath = File(root, "Tests.exe").path
        File(root, "Tests.dll").writeText("not a real assembly, just needs to exist")

        assertEquals(File(root, "Tests.dll").path, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    @Test
    fun `normalizeToManagedAssemblyPath falls back to the original path when no dll sibling exists`() {
        val root = tempDir()
        val exePath = File(root, "Tests").path

        assertEquals(exePath, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    // ── buildTestFilter ──────────────────────────────────────────────────────

    @Test
    fun `a single target produces one filter term`() {
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(listOf(target())))
    }

    @Test
    fun `row-tests targets sharing one method collapse to a single term`() {
        val targets = listOf(target(), target(), target())
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(targets))
    }

    @Test
    fun `individual-methods targets with distinct method names each get their own term`() {
        val targets = listOf(
            target(methodName = "CheckValue_1"),
            target(methodName = "CheckValue_2"),
        )
        assertEquals(
            "FullyQualifiedName=Tests.FFeature.CheckValue_1|FullyQualifiedName=Tests.FFeature.CheckValue_2",
            RunTestRunner.buildTestFilter(targets),
        )
    }

    @Test
    fun `an empty target list produces an empty filter`() {
        assertEquals("", RunTestRunner.buildTestFilter(emptyList()))
    }
}
