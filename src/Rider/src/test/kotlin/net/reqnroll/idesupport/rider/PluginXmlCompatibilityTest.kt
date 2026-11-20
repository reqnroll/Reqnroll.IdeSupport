package net.reqnroll.idesupport.rider

import org.w3c.dom.Element
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class PluginXmlCompatibilityTest {
    private val root: Element by lazy {
        val stream = javaClass.getResourceAsStream("/META-INF/plugin.xml")
        assertNotNull(stream, "plugin.xml is on the classpath")
        DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(stream).documentElement
    }

    private fun texts(tag: String): List<String> =
        (0 until root.getElementsByTagName(tag).length).map { root.getElementsByTagName(tag).item(it).textContent.trim() }

    @Test
    fun `the earlier Reqnroll plugin is declared incompatible`() {
        assertEquals(listOf("net.reqnroll"), texts("incompatible-with"))
    }

    @Test
    fun `the description no longer says both plugins can be enabled together`() {
        val description = texts("description").single()

        assertTrue("cannot be enabled together" in description, description)
        assertTrue("Only keep one enabled" !in description, description)
    }
}
