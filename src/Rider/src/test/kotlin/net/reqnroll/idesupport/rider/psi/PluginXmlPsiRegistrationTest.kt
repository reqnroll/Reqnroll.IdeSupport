package net.reqnroll.idesupport.rider.psi

import org.w3c.dom.Element
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Checks `plugin.xml` wires the PSI up as intended. Parsed as XML rather than loaded through a platform fixture,
 * which cannot open a project under Rider's test target; the class names are resolved so a typo fails here
 * instead of when the IDE loads the plugin.
 */
class PluginXmlPsiRegistrationTest {
    private val extensions: Element by lazy {
        val stream = javaClass.getResourceAsStream("/META-INF/plugin.xml")
        assertNotNull(stream, "plugin.xml is on the classpath")
        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(stream)
        document.getElementsByTagName("extensions").item(0) as Element
    }

    private fun registrations(name: String): List<Element> =
        (0 until extensions.getElementsByTagName(name).length).map { extensions.getElementsByTagName(name).item(it) as Element }

    @Test
    fun `the parser definition is registered for Gherkin and resolves`() {
        val definition = registrations("lang.parserDefinition").single()

        assertEquals("Gherkin", definition.getAttribute("language"))
        assertEquals(ReqnrollFeatureParserDefinition::class.java, Class.forName(definition.getAttribute("implementationClass")))
    }

    @Test
    fun `the spellchecking strategy is registered for Gherkin and resolves`() {
        val strategy = registrations("spellchecker.support").single()

        assertEquals("Gherkin", strategy.getAttribute("language"))
        assertEquals(ReqnrollFeatureSpellcheckingStrategy::class.java, Class.forName(strategy.getAttribute("implementationClass")))
    }

    @Test
    fun `the debugger support policy is registered for Gherkin only`() {
        val policies = registrations("rider.debuggerSupportPolicy")

        assertEquals(listOf("Gherkin"), policies.map { it.getAttribute("language") })
        assertTrue(policies.all { Class.forName(it.getAttribute("implementationClass")) != null })
    }
}
