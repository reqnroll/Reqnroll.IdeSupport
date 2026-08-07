package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class RunTestRunnerTest {
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
    fun `returns null when no project folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj")
        assertNull(RunTestRunner.findOwningProjectPath("/elsewhere/A.feature", projects))
    }

    @Test
    fun `returns null for an empty project list`() {
        assertNull(RunTestRunner.findOwningProjectPath("/repo/Foo/A.feature", emptyList()))
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
