package net.reqnroll.idesupport.rider.lsp.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ProjectFileRoleTest {
    @Test
    fun `classify recognizes feature files case-insensitively`() {
        assertEquals(ProjectFileRole.FEATURE, ProjectFileRole.classify("/repo/Calculator.feature"))
        assertEquals(ProjectFileRole.FEATURE, ProjectFileRole.classify("/repo/Calculator.FEATURE"))
    }

    @Test
    fun `classify recognizes cs files case-insensitively`() {
        assertEquals(ProjectFileRole.BINDING, ProjectFileRole.classify("/repo/CalculatorSteps.cs"))
        assertEquals(ProjectFileRole.BINDING, ProjectFileRole.classify("/repo/CalculatorSteps.CS"))
    }

    @Test
    fun `classify returns null for untracked extensions`() {
        assertNull(ProjectFileRole.classify("/repo/README.md"))
        assertNull(ProjectFileRole.classify("/repo/project.csproj"))
        assertNull(ProjectFileRole.classify("/repo/noextension"))
    }
}
