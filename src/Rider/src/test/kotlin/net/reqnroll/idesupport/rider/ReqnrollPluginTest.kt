package net.reqnroll.idesupport.rider

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ReqnrollPluginTest {
    @Test
    fun `reads the plugin version Gradle patches into plugin xml`() {
        val xml = """
            <idea-plugin>
              <id>net.reqnroll.idesupport</id>
              <version>2026.2.1500</version>
              <idea-version since-build="243"/>
            </idea-plugin>
        """.trimIndent()

        assertEquals("2026.2.1500", ReqnrollPlugin.versionIn(xml))
    }

    @Test
    fun `tolerates whitespace around the version`() {
        assertEquals("1.2.3", ReqnrollPlugin.versionIn("<version>\n  1.2.3 \n</version>"))
    }

    @Test
    fun `a plugin xml without a version has none`() {
        assertNull(ReqnrollPlugin.versionIn("<idea-plugin><id>x</id></idea-plugin>"))
    }
}
