package net.reqnroll.idesupport.rider.testrunner

import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class DotnetTestModeDetectorTest {
    private val tempDirs = mutableListOf<File>()

    @AfterTest
    fun cleanUpTempDirs() {
        tempDirs.forEach { it.deleteRecursively() }
    }

    private fun tempDir(): File {
        val dir = Files.createTempDirectory("reqnroll-mode-detector-test").toFile()
        tempDirs += dir
        return dir
    }

    // ── isMtpCapable: text scan ──────────────────────────────────────────────

    private fun projectFile(root: File, xml: String, relativePath: String = "Proj/Proj.csproj"): File {
        val file = File(root, relativePath)
        file.parentFile.mkdirs()
        file.writeText(xml)
        return file
    }

    @Test
    fun `isMtpCapable is true when the project itself sets EnableMSTestRunner and TestingPlatformDotnetTestSupport`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertTrue(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is true when the project sets EnableNUnitRunner and dotnet test opts into native MTP mode via global json`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>")
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertTrue(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is true when only MTP-capable, with no evidence dotnet test actually redirects to it`() {
        // On the .NET 10 SDK, Microsoft.Testing.Platform.MSBuild.targets rejects every dotnet test of
        // such a project ("Testing with VSTest target is no longer supported..."), so a missing TRX is
        // not a launch failure. detect still picks VS_TEST for it; the detect tests cover that.
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup></Project>",
        )

        assertTrue(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is false when only the dotnet test redirect is set, with no MTP-capable framework`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>")

        assertFalse(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is false for a plain VSTest project with no Directory Build props`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")

        assertFalse(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is false when EnableMSTestRunner is explicitly false`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>false</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertFalse(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable finds a repo-root Directory Build props the project itself doesn't set`() {
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>" +
                "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>"
        )
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertTrue(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable combines a capability signal from one file with a redirect signal from another`() {
        // EnableMSTestRunner in the project itself, TestingPlatformDotnetTestSupport only in the
        // repo-root Directory.Build.props — both must be honored even though they come from
        // different files along the ancestor chain.
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>"
        )
        val project = projectFile(root, "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertTrue(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable does not climb above the git root`() {
        val outside = tempDir()
        File(outside, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>"
        )
        val repo = File(outside, "repo")
        File(repo, ".git").mkdirs()
        val project = projectFile(repo, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertFalse(DotnetTestModeDetector.isMtpCapable(project.path) { null })
    }

    @Test
    fun `isMtpCapable is false for a project file that does not exist and no props anywhere`() {
        val root = tempDir()

        assertFalse(DotnetTestModeDetector.isMtpCapable(File(root, "Missing/Missing.csproj").path) { null })
    }

    // ── detect ───────────────────────────────────────────────────────────────

    @Test
    fun `detect is VS_TEST for a plain project`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")

        assertEquals(DotnetTestMode.VS_TEST, DotnetTestModeDetector.detect(project.path) { null })
    }

    @Test
    fun `detect is VS_TEST when MTP-capable but nothing redirects dotnet test to it`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>")

        assertEquals(DotnetTestMode.VS_TEST, DotnetTestModeDetector.detect(project.path) { null })
    }

    @Test
    fun `detect is MTP_COMPAT when TestingPlatformDotnetTestSupport is set and no native mode`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertEquals(DotnetTestMode.MTP_COMPAT, DotnetTestModeDetector.detect(project.path) { null })
    }

    @Test
    fun `detect is MTP_NATIVE when global json opts into native mode, even with TestingPlatformDotnetTestSupport also set`() {
        // Native mode takes precedence over the compat redirect when both are present.
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertEquals(DotnetTestMode.MTP_NATIVE, DotnetTestModeDetector.detect(project.path) { null })
    }

    @Test
    fun `detect is MTP_NATIVE from a global json found only by climbing to the repo root`() {
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")
        val project = projectFile(root, "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertEquals(DotnetTestMode.MTP_NATIVE, DotnetTestModeDetector.detect(project.path) { null })
    }

    @Test
    fun `detect is VS_TEST when global json opts into native mode but the project is not MTP-capable`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertEquals(DotnetTestMode.VS_TEST, DotnetTestModeDetector.detect(project.path) { null })
    }

    // ── detect: MSBuild-evaluated signal ─────────────────────────────────────

    @Test
    fun `detect trusts a true MSBuild evaluation even when the project file text says nothing`() {
        // Regression guard: a project made MTP-capable only through an imported props file
        // (e.g. referencing the full xunit.v3 runner package) has none of the marker properties as
        // literal text anywhere a scan would look, but a real MSBuild evaluation sees it.
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = DotnetTestModeDetector.detect(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = true)
        }

        assertEquals(DotnetTestMode.MTP_COMPAT, mode)
    }

    @Test
    fun `detect is VS_TEST when the MSBuild evaluation says mtpCapable but nothing redirects`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = DotnetTestModeDetector.detect(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.VS_TEST, mode)
    }

    @Test
    fun `detect combines a true MSBuild evaluation with a native global json opt-in`() {
        val root = tempDir()
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = DotnetTestModeDetector.detect(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.MTP_NATIVE, mode)
    }

    @Test
    fun `detect falls back to the text scan when the MSBuild evaluation is unavailable`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        val mode = DotnetTestModeDetector.detect(project.path) { null }

        assertEquals(DotnetTestMode.MTP_COMPAT, mode)
    }

    @Test
    fun `detect does not let a Directory Build props text match override a false MSBuild evaluation`() {
        // The MSBuild evaluation already reflects every imported props file's real, Condition-aware
        // resolved value — a plain text match in Directory.Build.props (which could sit inside a
        // Condition that never actually applies) must not override a real "not capable" result.
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>"
        )
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        val mode = DotnetTestModeDetector.detect(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = false, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.VS_TEST, mode)
    }

    // ── isMtpCapable: MSBuild-evaluated signal ───────────────────────────────

    @Test
    fun `isMtpCapable is true from a true MSBuild evaluation even without a redirect signal`() {
        // IsTestingPlatformApplication=true with no redirect: detect still picks VS_TEST, but
        // isMtpCapable must be true so a missing TRX reads as Inconclusive, not Failure.
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val capable = DotnetTestModeDetector.isMtpCapable(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertTrue(capable)
    }

    @Test
    fun `isMtpCapable is false from a false MSBuild evaluation`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val capable = DotnetTestModeDetector.isMtpCapable(project.path) {
            DotnetTestModeDetector.MtpEvaluation(mtpCapable = false, dotnetTestRedirectsToMtp = false)
        }

        assertFalse(capable)
    }

    // ── evaluateMtpPropertiesViaMsbuild ──────────────────────────────────────

    @Test
    fun `evaluateMtpPropertiesViaMsbuild returns null for a project path that cannot be evaluated`() {
        // No real dotnet/MSBuild project exists at this path — exercises the "process fails/exits
        // non-zero" branch without asserting anything about a specific error message.
        val root = tempDir()
        val missing = File(root, "Missing/Missing.csproj").path

        assertNull(DotnetTestModeDetector.evaluateMtpPropertiesViaMsbuild(missing))
    }
}
