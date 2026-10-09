package com.reqnroll.ide.rider.testrunner

import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class RunTestResultStoreTest {
    private val uriA = "file:///repo/A.feature"
    private val uriB = "file:///repo/B.feature"

    @AfterTest
    fun cleanUp() {
        RunTestResultStore.clearFile(uriA)
        RunTestResultStore.clearFile(uriB)
    }

    @Test
    fun `an edit to a file drops its results so a shifted scenario cannot inherit a stale glyph`() {
        // Scenario "Pass" ran at line 3 and passed; the lens at line 3 shows it.
        RunTestResultStore.set(uriA, 3, RunResult(RunOutcome.PASSED))
        RunTestResultStore.set(uriA, 9, RunResult(RunOutcome.FAILED))
        assertEquals(RunOutcome.PASSED, RunTestResultStore.get(uriA, 3)?.outcome)

        // A line is inserted above: a different scenario now sits where the old result was recorded.
        RunTestResultStore.clearFile(uriA)

        assertNull(RunTestResultStore.get(uriA, 3))
        assertNull(RunTestResultStore.get(uriA, 9))
    }

    @Test
    fun `clearing one file keeps the results of unedited files`() {
        RunTestResultStore.set(uriA, 3, RunResult(RunOutcome.PASSED))
        RunTestResultStore.set(uriB, 3, RunResult(RunOutcome.FAILED))

        RunTestResultStore.clearFile(uriA)

        assertNull(RunTestResultStore.get(uriA, 3))
        assertEquals(RunOutcome.FAILED, RunTestResultStore.get(uriB, 3)?.outcome)
    }

    @Test
    fun `the lens entry for a shifted scenario does not show the old result after the file is cleared`() {
        RunTestResultStore.set(uriA, 3, RunResult(RunOutcome.FAILED))
        RunTestResultStore.clearFile(uriA)
        assertEquals("▶ Run", RunLensSupport.renderTitle(RunTestResultStore.get(uriA, 3)?.outcome))
    }
}
