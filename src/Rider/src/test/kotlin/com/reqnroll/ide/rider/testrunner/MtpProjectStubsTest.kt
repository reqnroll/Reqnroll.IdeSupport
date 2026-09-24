package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.ReqnrollMtpReporterPathResolver
import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Covers [MtpProjectStubs] — where and when Rider writes the issue #741 project-local
 * `obj/<Project>.csproj.reqnroll-ide.targets` stub. What the stub's import does to a build is covered by
 * `SourceInjectionBuildTests` in Reqnroll.IdeSupport.TestReporter.MTP.Tests.
 */
class MtpProjectStubsTest {
    private val tempDirs = mutableListOf<File>()
    private val bundleTargets = "/ext/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.targets"
    private val noEvaluation: (String) -> String? = { error("must not evaluate on the fast path") }

    @AfterTest
    fun cleanUpTempDirs() {
        tempDirs.forEach { it.deleteRecursively() }
    }

    private fun tempDir(): File = Files.createTempDirectory("reqnroll-mtp-project-stubs-test").toFile().also { tempDirs += it }

    private fun project(root: File, relativePath: String, xml: String = "<Project Sdk=\"Microsoft.NET.Sdk\" />"): File =
        File(root, relativePath).apply {
            parentFile.mkdirs()
            writeText(xml)
        }

    @Test
    fun `the stub is an Exists-guarded import of the bundle and nothing else`() {
        val xml = MtpProjectStubs.buildStubXml(bundleTargets)

        assertTrue(xml.contains("<Import Project=\"$bundleTargets\" Condition=\"Exists('$bundleTargets')\" />"))
        assertFalse(xml.contains("<Reference"))
        assertFalse(xml.contains("TestingPlatformBuilderHook"))
    }

    @Test
    fun `an ordinary project gets obj without an MSBuild evaluation`() {
        val root = tempDir()
        val csproj = project(root, "App/App.csproj")

        assertEquals(File(root, "App/obj").absoluteFile, MtpProjectStubs.resolveProjectExtensionsDirectory(csproj.path, evaluate = noEvaluation))
    }

    @Test
    fun `a Directory Build props that moves obj defers to MSBuild`() {
        val root = tempDir()
        File(root, "Directory.Build.props").writeText("<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>")
        val csproj = project(root, "src/App/App.csproj")
        val evaluated = File(root, "artifacts/obj/App").absolutePath

        assertEquals(File(evaluated), MtpProjectStubs.resolveProjectExtensionsDirectory(csproj.path) { evaluated })
    }

    @Test
    fun `a failed evaluation means no stub rather than a guess`() {
        val root = tempDir()
        val csproj = project(root, "App/App.csproj", "<Project><PropertyGroup><BaseIntermediateOutputPath>x/</BaseIntermediateOutputPath></PropertyGroup></Project>")

        assertNull(MtpProjectStubs.writeStub(csproj.path, bundleTargets) { null })
        assertFalse(File(root, "App/obj").exists())
    }

    @Test
    fun `writeStub writes obj Project csproj reqnroll-ide targets and leaves an up-to-date stub untouched`() {
        val root = tempDir()
        val csproj = project(root, "App/App.csproj")

        val stub = assertNotNull(MtpProjectStubs.writeStub(csproj.path, bundleTargets, evaluate = noEvaluation))
        assertEquals(File(root, "App/obj/App.csproj.reqnroll-ide.targets").absoluteFile, stub.absoluteFile)
        assertEquals(MtpProjectStubs.buildStubXml(bundleTargets), stub.readText())

        stub.setLastModified(1_000_000_000_000L)
        MtpProjectStubs.writeStub(csproj.path, bundleTargets, evaluate = noEvaluation)
        assertEquals(1_000_000_000_000L, stub.lastModified(), "rewriting an unchanged stub would disturb incremental builds")
    }

    @Test
    fun `writeStub refreshes a stub pointing at an older plugin install`() {
        val root = tempDir()
        val csproj = project(root, "App/App.csproj")
        MtpProjectStubs.writeStub(csproj.path, "/old/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.targets", evaluate = noEvaluation)

        val stub = assertNotNull(MtpProjectStubs.writeStub(csproj.path, bundleTargets, evaluate = noEvaluation))

        assertTrue(stub.readText().contains(bundleTargets))
        assertFalse(stub.readText().contains("/old/"))
    }

    @Test
    fun `non-CSharp projects get no stub`() {
        val root = tempDir()
        val vbproj = project(root, "App/App.vbproj")

        assertNull(MtpProjectStubs.writeStub(vbproj.path, bundleTargets, evaluate = noEvaluation))
        assertFalse(File(root, "App/obj").exists())
    }

    @Test
    fun `the resolver needs both the bundle targets and its sources`() {
        val bundle = tempDir().toPath()
        assertNull(ReqnrollMtpReporterPathResolver.resolveIn(bundle))

        val targets = Files.writeString(bundle.resolve(ReqnrollMtpReporterPathResolver.BUNDLE_TARGETS_FILE_NAME), "<Project />")
        assertNull(ReqnrollMtpReporterPathResolver.resolveIn(bundle), "an incomplete bundle must not be used")

        Files.createDirectories(bundle.resolve(ReqnrollMtpReporterPathResolver.SOURCE_DIRECTORY_NAME))
        assertEquals(targets, ReqnrollMtpReporterPathResolver.resolveIn(bundle))
    }
}
