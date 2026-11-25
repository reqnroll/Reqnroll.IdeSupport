package net.reqnroll.idesupport.rider.testrunner

import net.reqnroll.idesupport.rider.lsp.protocol.RegisterTestRunResponse
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

class DotnetTestModeStrategyTest {
    private val registration = RegisterTestRunResponse(success = true, runId = "run-1", endpoint = "127.0.0.1:5000")
    private val loggerDirectory: Path = Path.of("logger-dir")
    private val strategies = mutableListOf<DotnetTestModeStrategy>()

    @AfterTest
    fun cleanUp() {
        strategies.forEach { it.cleanUp() }
    }

    private fun vsTest(
        registration: RegisterTestRunResponse? = this.registration,
        loggerDirectory: Path? = this.loggerDirectory,
        isMtpCapable: Boolean = false,
    ) = VsTestStrategy(registration, loggerDirectory) { isMtpCapable }.also { strategies += it }

    private fun resultsDirectory(arguments: List<String>) =
        File(arguments[arguments.indexOf("--results-directory") + 1])

    // ── buildLoggerArgument ──────────────────────────────────────────────────

    @Test
    fun `buildLoggerArgument matches the friendly name and parameter keys VS injects into runsettings`() {
        assertEquals(
            "ReqnrollIde;Endpoint=127.0.0.1:5000;RunId=run-1;IdeProcessId=4242",
            VsTestStrategy.buildLoggerArgument(registration, ideProcessId = 4242L),
        )
    }

    // ── VsTestStrategy ───────────────────────────────────────────────────────

    @Test
    fun `VSTest arguments add the bundled logger only when registered and the logger was found`() {
        val withLogger = vsTest().arguments()
        assertEquals(listOf("--nologo", "--logger"), withLogger.take(2))
        assertEquals("logger-dir", withLogger[withLogger.indexOf("--test-adapter-path") + 1])
        assertTrue(withLogger.any { it.startsWith("ReqnrollIde;Endpoint=127.0.0.1:5000;RunId=run-1;") })

        assertFalse("--test-adapter-path" in vsTest(registration = null, loggerDirectory = null).arguments())
        assertFalse("--test-adapter-path" in vsTest(loggerDirectory = null).arguments())
    }

    @Test
    fun `VSTest reports to the server only when registered and the logger was found`() {
        assertTrue(vsTest().reportsToServer)
        assertFalse(vsTest(loggerDirectory = null).reportsToServer)
        assertFalse(vsTest(registration = null, loggerDirectory = null).reportsToServer)
    }

    @Test
    fun `VSTest reads the TRX regardless of the exit code`() {
        val strategy = vsTest()
        val trx = File(resultsDirectory(strategy.arguments()), "result.trx")
        trx.writeText(
            """<TestRun><Results><UnitTestResult testName="a" outcome="Passed"/>""" +
                """<UnitTestResult testName="b" outcome="Failed"/></Results></TestRun>""",
        )

        val outcome = strategy.interpret("Proj.csproj", exitCode = 1)

        assertEquals(DotnetTestOutcome.Completed(RunResult(RunOutcome.FAILED)), outcome)
    }

    @Test
    fun `VSTest without a TRX is a failure for a plain project and inconclusive for an MTP-capable one`() {
        val plain = vsTest().also { it.arguments() }
        assertIs<DotnetTestOutcome.Failure>(plain.interpret("Proj.csproj", exitCode = 0))

        val mtpCapable = vsTest(isMtpCapable = true).also { it.arguments() }
        assertIs<DotnetTestOutcome.Inconclusive>(mtpCapable.interpret("Proj.csproj", exitCode = 1))
    }

    @Test
    fun `VSTest cleanUp deletes its results directory`() {
        val strategy = vsTest()
        val directory = resultsDirectory(strategy.arguments())
        assertTrue(directory.isDirectory)

        strategy.cleanUp()

        assertFalse(directory.exists())
    }

    @Test
    fun `VSTest cleanUp is harmless when the run never built its arguments`() {
        vsTest().cleanUp()
    }

    // ── MtpStrategy ──────────────────────────────────────────────────────────

    @Test
    fun `MTP adds no arguments, since native mode rejects --logger and --nologo`() {
        assertEquals(emptyList(), MtpStrategy(Path.of("bundle.targets")).arguments())
        assertEquals(emptyList(), MtpStrategy(null).arguments())
    }

    @Test
    fun `MTP reports to the server only when the reporter bundle was found`() {
        assertTrue(MtpStrategy(Path.of("bundle.targets")).reportsToServer)
        assertFalse(MtpStrategy(null).reportsToServer)
    }

    @Test
    fun `MTP takes its result from the exit code`() {
        val strategy = MtpStrategy(null)
        assertEquals(DotnetTestOutcome.Completed(RunResult(RunOutcome.PASSED)), strategy.interpret("Proj.csproj", 0))
        assertEquals(DotnetTestOutcome.Completed(RunResult(RunOutcome.FAILED)), strategy.interpret("Proj.csproj", 2))
    }

    @Test
    fun `MTP prepare writes nothing without a reporter bundle`() {
        val root = Files.createTempDirectory("reqnroll-mtp-strategy-test").toFile()
        try {
            val project = File(root, "Proj.csproj").apply { writeText("<Project />") }

            MtpStrategy(null).prepare(project.path)

            assertEquals(listOf("Proj.csproj"), root.walk().filter { it.isFile }.map { it.name }.toList())
        } finally {
            root.deleteRecursively()
        }
    }
}
