package net.reqnroll.idesupport.rider.psi

import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.createDirectories
import kotlin.io.path.writeText
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals

class FeatureDefaultLanguagesTest {
    private lateinit var root: Path

    @BeforeTest
    fun setUp() {
        root = Files.createTempDirectory("feature-language")
    }

    @AfterTest
    fun tearDown() {
        root.toFile().deleteRecursively()
    }

    private fun project(name: String, config: Pair<String, String>? = null): Path {
        val dir = root.resolve(name).also { it.createDirectories() }
        dir.resolve("$name.csproj").writeText("<Project />")
        config?.let { (file, content) -> dir.resolve(file).writeText(content) }
        return dir
    }

    private fun featureIn(dir: Path, relative: String = "Login.feature"): String =
        dir.resolve(relative).toString()

    @Test
    fun `reads language feature from reqnroll json next to the project`() {
        val dir = project("App", "reqnroll.json" to """{ "language": { "feature": "fr-FR" } }""")

        assertEquals("fr-FR", FeatureDefaultLanguages.languageForPath(featureIn(dir)))
    }

    @Test
    fun `reads specflow json when there is no reqnroll json`() {
        val dir = project("App", "specflow.json" to """{ "language": { "feature": "de" } }""")

        assertEquals("de", FeatureDefaultLanguages.languageForPath(featureIn(dir)))
    }

    @Test
    fun `finds the configuration from a feature in a sub-folder`() {
        val dir = project("App", "reqnroll.json" to """{ "language": { "feature": "no" } }""")
        dir.resolve("Features/Login").createDirectories()

        assertEquals("no", FeatureDefaultLanguages.languageForPath(featureIn(dir, "Features/Login/A.feature")))
    }

    @Test
    fun `falls back to English when the project has no configuration`() {
        val dir = project("App")

        assertEquals("en", FeatureDefaultLanguages.languageForPath(featureIn(dir)))
    }

    @Test
    fun `does not read a configuration above the project folder`() {
        root.resolve("reqnroll.json").writeText("""{ "language": { "feature": "fr" } }""")
        val dir = project("App")

        assertEquals("en", FeatureDefaultLanguages.languageForPath(featureIn(dir)))
    }

    @Test
    fun `falls back to English for a missing language setting or malformed json`() {
        val withoutLanguage = project("A", "reqnroll.json" to """{ "bindingCulture": { "name": "en-US" } }""")
        val malformed = project("B", "reqnroll.json" to "{ not json")

        assertEquals("en", FeatureDefaultLanguages.languageForPath(featureIn(withoutLanguage)))
        assertEquals("en", FeatureDefaultLanguages.languageForPath(featureIn(malformed)))
    }

    @Test
    fun `picks up an edited configuration`() {
        val dir = project("App", "reqnroll.json" to """{ "language": { "feature": "fr" } }""")
        val feature = featureIn(dir)
        assertEquals("fr", FeatureDefaultLanguages.languageForPath(feature))

        val config = dir.resolve("reqnroll.json")
        config.writeText("""{ "language": { "feature": "de" } }""")
        Files.setLastModifiedTime(config, java.nio.file.attribute.FileTime.fromMillis(System.currentTimeMillis() + 5000))

        assertEquals("de", FeatureDefaultLanguages.languageForPath(feature))
    }

    @Test
    fun `a null or non-local file uses English`() {
        assertEquals("en", FeatureDefaultLanguages.languageFor(null))
    }
}
