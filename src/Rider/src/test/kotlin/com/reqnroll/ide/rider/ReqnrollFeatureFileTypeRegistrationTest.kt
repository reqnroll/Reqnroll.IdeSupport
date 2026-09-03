package com.reqnroll.ide.rider

import com.intellij.openapi.fileTypes.FileTypeManager
import com.intellij.testFramework.ApplicationRule
import org.junit.ClassRule
import org.junit.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals

/**
 * Confirms `.feature` actually resolves to [ReqnrollFeatureFileType]/[ReqnrollFeatureLanguage]
 * through plugin.xml's `fileType` extension at runtime, not just via direct class references —
 * the class of bug (a `plugin.xml` wiring typo, e.g. a mismatched `language` attribute) that
 * `verifyPlugin`'s bytecode-level checks don't catch, since they never actually load the
 * extension point.
 *
 * [FileTypeManager] is an *application*-level service, so this only needs [ApplicationRule] (no
 * [com.intellij.testFramework.fixtures.BasePlatformTestCase] fixture / [Project] involved) —
 * deliberately: opening any real Project under this plugin's `intellijPlatform { rider(...) }`
 * target eagerly initializes Rider's own `ClientProjectSessionsManager` project service, which
 * throws `PluginException: solution can't be null` for a fixture project with no real backend
 * solution attached (confirmed live via `./gradlew test` — every `BasePlatformTestCase` test
 * failed with that same cause, regardless of what it actually exercised). Real coverage of
 * anything that needs a live `Project` (`ReqnrollLspServerSupportProvider.fileOpened`,
 * `ReqnrollLspServerDescriptor.isSupportedFile`, the `ProjectActivity` wiring classes) needs
 * JetBrains' TestNG-based `resharper-test-framework` (`BaseTestWithSolution` and friends, which
 * spin up a real backend against a real solution) instead — a materially bigger lift (different
 * test runner, real backend startup, `.sln`-shaped test data) than `testFramework(TestFrameworkType.Platform)`
 * alone provides, and out of scope for this pass; see CONTRIBUTING.md's "Testing" section.
 */
class ReqnrollFeatureFileTypeRegistrationTest {
    companion object {
        @JvmField
        @ClassRule
        val applicationRule = ApplicationRule()
    }

    @Test
    fun `feature extension resolves to ReqnrollFeatureFileType`() {
        val fileType = FileTypeManager.getInstance().getFileTypeByFileName("Test.feature")

        assertEquals(ReqnrollFeatureFileType, fileType)
    }

    @Test
    fun `ReqnrollFeatureFileType resolves to the Gherkin language`() {
        assertEquals(ReqnrollFeatureLanguage, ReqnrollFeatureFileType.language)
    }

    @Test
    fun `other extensions do not resolve to ReqnrollFeatureFileType`() {
        val fileType = FileTypeManager.getInstance().getFileTypeByFileName("Test.txt")

        assertNotEquals(ReqnrollFeatureFileType, fileType)
    }
}
