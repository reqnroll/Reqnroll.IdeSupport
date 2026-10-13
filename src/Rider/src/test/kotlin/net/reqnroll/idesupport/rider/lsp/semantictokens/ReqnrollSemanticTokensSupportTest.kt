package net.reqnroll.idesupport.rider.lsp.semantictokens

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class ReqnrollSemanticTokensSupportTest {
    @Test
    fun `ORDERED has 11 distinct reqnroll-prefixed names`() {
        assertEquals(11, ReqnrollSemanticTokenTypes.ORDERED.size)
        assertEquals(ReqnrollSemanticTokenTypes.ORDERED.size, ReqnrollSemanticTokenTypes.ORDERED.toSet().size)
        assertTrue(ReqnrollSemanticTokenTypes.ORDERED.all { it.startsWith("reqnroll.") })
    }

    @Test
    fun `every ORDERED type has a TextAttributesKey mapping - none silently fall through`() {
        val support = ReqnrollSemanticTokensSupport()
        ReqnrollSemanticTokenTypes.ORDERED.forEach { type ->
            assertNotNull(
                support.getTextAttributesKey(type, emptyList()),
                "no TextAttributesKey mapped for $type — would silently render with no color",
            )
        }
    }

    @Test
    fun `an unrecognized type name returns null rather than a misleading default`() {
        val support = ReqnrollSemanticTokensSupport()
        assertEquals(null, support.getTextAttributesKey("not.a.real.type", emptyList()))
    }

    @Test
    fun `tokenTypes property matches ORDERED so the platform requests every custom type`() {
        val support = ReqnrollSemanticTokensSupport()
        assertEquals(ReqnrollSemanticTokenTypes.ORDERED, support.tokenTypes)
    }
}
