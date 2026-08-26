package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** Coverage for issue #495: [RunTestTargetCache] is the only lever Rider has for skipping repeat `reqnroll/resolveTestTargets` calls, since `CodeVisionProvider` gives it no per-line/visible-range hook the way VS's classic CodeLens and VS Code's `resolveCodeLens` do. */
class RunTestTargetCacheTest {
    @AfterTest
    fun cleanUp() = RunTestTargetCache.invalidateAll()

    private fun targets(vararg methodNames: String) =
        methodNames.map { ScenarioTestTargetItem(declaringTypeFullName = "Tests.FFeature", methodName = it) }

    @Test
    fun `get returns null for a line never cached`() {
        assertNull(RunTestTargetCache.get("file:///F.feature", 1, "Scenario|S"))
    }

    @Test
    fun `get returns the cached targets when the identity matches`() {
        val cached = targets("AddNumbers")
        RunTestTargetCache.put("file:///F.feature", 1, "Scenario|S", cached)

        assertEquals(cached, RunTestTargetCache.get("file:///F.feature", 1, "Scenario|S"))
    }

    @Test
    fun `get returns null when the identity at that line has changed`() {
        RunTestTargetCache.put("file:///F.feature", 1, "Scenario|S", targets("AddNumbers"))

        // The scenario at line 1 was renamed (or changed Scenario/Outline kind) since the cache
        // entry was written — the old resolution must not be handed back as if still valid.
        assertNull(RunTestTargetCache.get("file:///F.feature", 1, "Scenario|Renamed"))
    }

    @Test
    fun `different lines in the same file are cached independently`() {
        RunTestTargetCache.put("file:///F.feature", 1, "Scenario|A", targets("A"))
        RunTestTargetCache.put("file:///F.feature", 7, "Scenario|B", targets("B"))

        assertEquals(targets("A"), RunTestTargetCache.get("file:///F.feature", 1, "Scenario|A"))
        assertEquals(targets("B"), RunTestTargetCache.get("file:///F.feature", 7, "Scenario|B"))
    }

    @Test
    fun `different files are cached independently even with the same line and identity`() {
        RunTestTargetCache.put("file:///A.feature", 1, "Scenario|S", targets("A"))
        RunTestTargetCache.put("file:///B.feature", 1, "Scenario|S", targets("B"))

        assertEquals(targets("A"), RunTestTargetCache.get("file:///A.feature", 1, "Scenario|S"))
        assertEquals(targets("B"), RunTestTargetCache.get("file:///B.feature", 1, "Scenario|S"))
    }

    @Test
    fun `invalidateFile drops every cached line for that file only`() {
        RunTestTargetCache.put("file:///A.feature", 1, "Scenario|S", targets("A"))
        RunTestTargetCache.put("file:///A.feature", 7, "Scenario|T", targets("B"))
        RunTestTargetCache.put("file:///B.feature", 1, "Scenario|S", targets("C"))

        RunTestTargetCache.invalidateFile("file:///A.feature")

        assertNull(RunTestTargetCache.get("file:///A.feature", 1, "Scenario|S"))
        assertNull(RunTestTargetCache.get("file:///A.feature", 7, "Scenario|T"))
        assertEquals(targets("C"), RunTestTargetCache.get("file:///B.feature", 1, "Scenario|S"))
    }

    @Test
    fun `invalidateAll drops every cached entry across every file`() {
        RunTestTargetCache.put("file:///A.feature", 1, "Scenario|S", targets("A"))
        RunTestTargetCache.put("file:///B.feature", 1, "Scenario|S", targets("B"))

        RunTestTargetCache.invalidateAll()

        assertNull(RunTestTargetCache.get("file:///A.feature", 1, "Scenario|S"))
        assertNull(RunTestTargetCache.get("file:///B.feature", 1, "Scenario|S"))
    }

    @Test
    fun `an empty resolved-targets list is still a cache hit, not treated as never-cached`() {
        RunTestTargetCache.put("file:///F.feature", 1, "Scenario|Unbound", emptyList())

        assertEquals(emptyList(), RunTestTargetCache.get("file:///F.feature", 1, "Scenario|Unbound"))
    }
}
