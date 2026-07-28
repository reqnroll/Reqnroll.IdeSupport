package com.reqnroll.ide.rider.lsp

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class DocumentStalenessTest {
    @Test
    fun `not stale when the current stamp matches the requested stamp`() {
        assertFalse(isDocumentStale(currentModificationStamp = 42L, requestModificationStamp = 42L))
    }

    @Test
    fun `stale when the document was edited after the request was sent (issue #326)`() {
        // The exact race this guards against: the stamp captured before firing the LSP request no
        // longer matches the document's live stamp by the time the response comes back.
        assertTrue(isDocumentStale(currentModificationStamp = 43L, requestModificationStamp = 42L))
    }

    @Test
    fun `stale when the document became unresolvable after the request was sent`() {
        assertTrue(isDocumentStale(currentModificationStamp = null, requestModificationStamp = 42L))
    }

    @Test
    fun `stale when the document was unresolvable at request time but is available now`() {
        assertTrue(isDocumentStale(currentModificationStamp = 42L, requestModificationStamp = null))
    }

    @Test
    fun `not stale when the document was unresolvable both at request time and now`() {
        assertFalse(isDocumentStale(currentModificationStamp = null, requestModificationStamp = null))
    }
}
