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
    fun `the syntax highlighter and commenter are registered for Gherkin and resolve`() {
        val highlighter = registrations("lang.syntaxHighlighterFactory").single()
        val commenter = registrations("lang.commenter").single()

        assertEquals("Gherkin", highlighter.getAttribute("language"))
        assertEquals(
            ReqnrollFeatureSyntaxHighlighterFactory::class.java,
            Class.forName(highlighter.getAttribute("implementationClass")),
        )
        assertEquals("Gherkin", commenter.getAttribute("language"))
        assertEquals(ReqnrollFeatureCommenter::class.java, Class.forName(commenter.getAttribute("implementationClass")))
    }

    @Test
    fun `the config change listener is registered on the bulk file topic and resolves`() {
        val stream = javaClass.getResourceAsStream("/META-INF/plugin.xml")!!
        val document = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(stream)
        val listeners = (0 until document.getElementsByTagName("listener").length)
            .map { document.getElementsByTagName("listener").item(it) as Element }
            .filter { it.getAttribute("class") == FeatureLanguageConfigListener::class.java.name }

        assertEquals(listOf("com.intellij.openapi.vfs.newvfs.BulkFileListener"), listeners.map { it.getAttribute("topic") })
    }

    @Test
    fun `the debugger support policy is registered for Gherkin only`() {
        val policies = registrations("rider.debuggerSupportPolicy")

        assertEquals(listOf("Gherkin"), policies.map { it.getAttribute("language") })
        assertTrue(policies.all { Class.forName(it.getAttribute("implementationClass")) != null })
    }
}
