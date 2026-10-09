package com.reqnroll.ide.rider.lsp

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * Issue #990: refreshes run their LSP request on a pooled thread and render on the EDT, so two
 * overlapping refreshes can complete in either order. These tests drive both "threads" by hand,
 * so the completion order is chosen by the test rather than by the scheduler.
 */
class LatestResponseGateTest {
    private val background = ArrayDeque<() -> Unit>()
    private val edt = ArrayDeque<() -> Unit>()
    private val gate = LatestResponseGate(runInBackground = { background.addLast(it) }, runOnEdt = { edt.addLast(it) })
    private val applied = mutableListOf<String?>()

    private fun runBackground(index: Int) = background.removeAt(index)()

    private fun drainEdt() {
        while (edt.isNotEmpty()) edt.removeFirst()()
    }

    @Test
    fun `an older response that completes after a newer one is discarded`() {
        gate.fetchThenApply({ "older" }) { applied += it }
        gate.fetchThenApply({ "newer" }) { applied += it }

        runBackground(1) // the newer request completes first...
        drainEdt()
        runBackground(0) // ...and the slower, older one lands last
        drainEdt()

        assertEquals(listOf<String?>("newer"), applied)
    }

    @Test
    fun `responses that complete in request order are all applied`() {
        gate.fetchThenApply({ "first" }) { applied += it }
        runBackground(0)
        drainEdt()
        gate.fetchThenApply({ "second" }) { applied += it }
        runBackground(0)
        drainEdt()

        assertEquals(listOf<String?>("first", "second"), applied)
    }

    @Test
    fun `an older response rendered after a newer request was issued is discarded even if it completed first`() {
        gate.fetchThenApply({ "older" }) { applied += it }
        runBackground(0) // completes and queues its render...
        gate.fetchThenApply({ "newer" }) { applied += it } // ...but a newer request is issued before the EDT runs it
        drainEdt()
        runBackground(0)
        drainEdt()

        assertEquals(listOf<String?>("newer"), applied)
    }

    @Test
    fun `a response for a request in flight when the document was edited is discarded`() {
        gate.fetchThenApply({ "before edit" }) { applied += it }
        gate.invalidate() // document edited while the request was in flight
        runBackground(0)
        drainEdt()

        assertEquals(emptyList(), applied)
    }

    @Test
    fun `a request issued after an edit is applied`() {
        gate.invalidate()
        gate.fetchThenApply({ "after edit" }) { applied += it }
        runBackground(0)
        drainEdt()

        assertEquals(listOf<String?>("after edit"), applied)
    }

    @Test
    fun `a failed (null) response is not applied, so the current output is kept`() {
        gate.fetchThenApply<String>({ null }) { applied += it }
        runBackground(0)
        drainEdt()

        assertEquals(emptyList(), applied)
    }
}
