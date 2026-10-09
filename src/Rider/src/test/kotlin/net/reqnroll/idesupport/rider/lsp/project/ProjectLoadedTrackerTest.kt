package net.reqnroll.idesupport.rider.lsp.project

import net.reqnroll.idesupport.rider.lsp.protocol.ReqnrollProjectLoadedParams
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ProjectLoadedTrackerTest {
    private fun params(path: String, tfm: String = "net9.0") = ReqnrollProjectLoadedParams(
        workspaceFolder = "/repo",
        projectFile = path,
        projectFolder = "/repo",
        outputAssemblyPath = "/repo/bin/App.dll",
        targetFrameworkMoniker = tfm,
        defaultNamespace = "",
    )

    @Test
    fun `shouldSend is true the first time a project file is seen`() {
        val tracker = ProjectLoadedTracker()
        assertTrue(tracker.shouldSend("a.csproj", params("a.csproj")))
    }

    @Test
    fun `shouldSend is false for an identical resend - the startup-churn fix`() {
        val tracker = ProjectLoadedTracker()
        val p = params("a.csproj")
        assertTrue(tracker.shouldSend("a.csproj", p))
        assertFalse(tracker.shouldSend("a.csproj", p))
        assertFalse(tracker.shouldSend("a.csproj", p.copy()))
    }

    @Test
    fun `shouldSend is true again when the params genuinely change`() {
        val tracker = ProjectLoadedTracker()
        tracker.shouldSend("a.csproj", params("a.csproj", tfm = "net8.0"))
        assertTrue(tracker.shouldSend("a.csproj", params("a.csproj", tfm = "net9.0")))
    }

    @Test
    fun `each project file path is tracked independently`() {
        val tracker = ProjectLoadedTracker()
        assertTrue(tracker.shouldSend("a.csproj", params("a.csproj")))
        assertTrue(tracker.shouldSend("b.csproj", params("b.csproj")))
        assertFalse(tracker.shouldSend("a.csproj", params("a.csproj")))
    }

    @Test
    fun `removedSince returns paths no longer in the current set and stops tracking them`() {
        val tracker = ProjectLoadedTracker()
        tracker.shouldSend("a.csproj", params("a.csproj"))
        tracker.shouldSend("b.csproj", params("b.csproj"))

        assertEquals(setOf("b.csproj"), tracker.removedSince(setOf("a.csproj")))
        // Already removed once — a second identical diff must not report it again.
        assertEquals(emptySet(), tracker.removedSince(setOf("a.csproj")))
    }

    @Test
    fun `a project removed and then re-added is treated as new - shouldSend is true again`() {
        val tracker = ProjectLoadedTracker()
        val p = params("a.csproj")
        tracker.shouldSend("a.csproj", p)
        tracker.removedSince(emptySet())

        assertTrue(tracker.shouldSend("a.csproj", p))
    }

    @Test
    fun `removedSince with no prior tracking returns empty`() {
        val tracker = ProjectLoadedTracker()
        assertEquals(emptySet(), tracker.removedSince(setOf("a.csproj")))
    }
}
