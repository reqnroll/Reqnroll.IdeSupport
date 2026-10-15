package net.reqnroll.idesupport.rider.formatting

import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ExplicitReformatTrackerTest {
    private var now = 0L
    private val tracker = ExplicitReformatTracker { now }

    @Test
    fun `an unmarked file is implicit`() {
        assertFalse(tracker.consume("/proj/a.feature"))
    }

    @Test
    fun `a marked file is explicit once, then implicit again`() {
        tracker.mark(listOf("/proj/a.feature"))

        assertTrue(tracker.consume("/proj/a.feature"))
        assertFalse(tracker.consume("/proj/a.feature"))
    }

    @Test
    fun `a mark for one file does not cover another`() {
        tracker.mark(listOf("/proj/a.feature"))

        assertFalse(tracker.consume("/proj/b.feature"))
    }

    @Test
    fun `a marked directory covers files below it but not a sibling with the same prefix`() {
        tracker.mark(listOf("/proj/features/"))

        assertFalse(tracker.consume("/proj/features2/a.feature"))
        assertTrue(tracker.consume("/proj/features/sub/a.feature"))
    }

    @Test
    fun `a mark expires`() {
        tracker.mark(listOf("/proj/a.feature"))
        now += TimeUnit.SECONDS.toNanos(ExplicitReformatTracker.TTL_SECONDS) + 1

        assertFalse(tracker.consume("/proj/a.feature"))
    }

    @Test
    fun `only Reformat Code and the Reformat File dialog count as explicit actions`() {
        assertTrue(ExplicitReformatActionListener.isExplicitReformatActionId("ReformatCode"))
        assertTrue(ExplicitReformatActionListener.isExplicitReformatActionId("ShowReformatFileDialog"))
        assertFalse(ExplicitReformatActionListener.isExplicitReformatActionId("SaveAll"))
        assertFalse(ExplicitReformatActionListener.isExplicitReformatActionId(null))
    }
}
