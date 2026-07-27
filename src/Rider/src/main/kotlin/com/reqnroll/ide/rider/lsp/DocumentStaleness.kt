package com.reqnroll.ide.rider.lsp

/**
 * True when a document's live modification stamp no longer matches the stamp captured before an
 * async LSP request was sent -- i.e. the user edited the document while the request was in
 * flight, so any offsets in the response are no longer valid against the current text (issue
 * #326). Shared by [com.reqnroll.ide.rider.formatting.ReqnrollFeatureOnTypeFormattingHandler] and
 * [com.reqnroll.ide.rider.actions.RenameStepRunner], which both capture a document's
 * `modificationStamp` before firing a request and re-check it before applying the response.
 *
 * `null` is a legitimate stamp value here (e.g. the document couldn't be resolved), and two
 * `null`s compare equal -- "still can't resolve the document" isn't itself a change worth
 * discarding the response over; a caller with a `null` *current* stamp typically has nothing to
 * apply an edit to anyway.
 */
internal fun isDocumentStale(currentModificationStamp: Long?, requestModificationStamp: Long?): Boolean =
    currentModificationStamp != requestModificationStamp
