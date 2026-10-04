package com.reqnroll.ide.rider.taglinks

import org.eclipse.lsp4j.DocumentLink
import java.net.URI

/** A clickable tag: the 0-based, end-exclusive document offsets of the tag and the URL it opens. */
data class TagLinkRange(val start: Int, val end: Int, val target: String) {
    fun contains(offset: Int): Boolean = offset in start until end
}

/**
 * Pure logic of clickable tags (issue #755), kept apart from [ReqnrollFeatureTagLinkController] so it can be
 * unit-tested without an editor.
 */
object TagLinkSupport {
    /**
     * Only web links are opened: the target comes from `reqnroll.json`, which a cloned repository controls, so
     * `file:` and other handler schemes are refused.
     */
    fun isOpenableUrl(target: String?): Boolean {
        if (target.isNullOrEmpty()) return false
        val scheme = try {
            URI(target).scheme?.lowercase()
        } catch (_: Exception) {
            return false
        }
        return scheme == "http" || scheme == "https"
    }

    /**
     * Maps the server's `DocumentLink[]` to document offsets. [lineStartOffset] returns the offset of a 0-based
     * line start, or null for a line the document does not have (a response computed for an older version of the
     * text); links with a non-openable target or an out-of-range position are dropped.
     */
    fun toRanges(
        links: List<DocumentLink>,
        documentLength: Int,
        lineStartOffset: (Int) -> Int?,
    ): List<TagLinkRange> = links.mapNotNull { link ->
        val target = link.target
        if (!isOpenableUrl(target)) return@mapNotNull null
        val start = lineStartOffset(link.range.start.line)?.plus(link.range.start.character) ?: return@mapNotNull null
        val end = lineStartOffset(link.range.end.line)?.plus(link.range.end.character) ?: return@mapNotNull null
        if (start < 0 || end <= start || end > documentLength) return@mapNotNull null
        TagLinkRange(start, end, target)
    }

    /** The link covering [offset], if any. */
    fun linkAt(links: List<TagLinkRange>, offset: Int): TagLinkRange? = links.firstOrNull { it.contains(offset) }
}
