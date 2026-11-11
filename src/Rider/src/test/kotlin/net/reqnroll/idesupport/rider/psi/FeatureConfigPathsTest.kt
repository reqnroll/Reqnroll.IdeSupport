package net.reqnroll.idesupport.rider.psi

import java.nio.file.Paths
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class FeatureConfigPathsTest {
    private val projectDir = Paths.get("work", "App").toString()

    @Test
    fun `a reqnroll json or specflow json names its directory`() {
        assertEquals(projectDir, FeatureDefaultLanguages.configDirectoryOf(Paths.get("work", "App", "reqnroll.json").toString()))
        assertEquals(projectDir, FeatureDefaultLanguages.configDirectoryOf(Paths.get("work", "App", "specflow.json").toString()))
        assertEquals(projectDir, FeatureDefaultLanguages.configDirectoryOf(Paths.get("work", "App", "Reqnroll.JSON").toString()))
    }

    @Test
    fun `other files are not configuration`() {
        assertNull(FeatureDefaultLanguages.configDirectoryOf(Paths.get("work", "App", "appsettings.json").toString()))
        assertNull(FeatureDefaultLanguages.configDirectoryOf(Paths.get("work", "App", "Login.feature").toString()))
        assertNull(FeatureDefaultLanguages.configDirectoryOf(""))
    }

    @Test
    fun `a file is within its directory and sub-folders but not a sibling with the same prefix`() {
        assertTrue(FeatureDefaultLanguages.isWithin(projectDir, Paths.get("work", "App", "Login.feature").toString()))
        assertTrue(FeatureDefaultLanguages.isWithin(projectDir, Paths.get("work", "App", "Features", "A.feature").toString()))
        assertFalse(FeatureDefaultLanguages.isWithin(projectDir, Paths.get("work", "App2", "Login.feature").toString()))
        assertFalse(FeatureDefaultLanguages.isWithin(projectDir, Paths.get("work", "Other", "Login.feature").toString()))
    }
}
