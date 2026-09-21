package com.reqnroll.ide.rider.testrunner

import com.reqnroll.ide.rider.lsp.protocol.GetTestOutcomeResponse
import com.reqnroll.ide.rider.lsp.protocol.RegisterTestRunResponse
import com.reqnroll.ide.rider.lsp.protocol.ScenarioTestTargetItem
import com.reqnroll.ide.rider.lsp.protocol.TestOutcomeRowItem
import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RunTestRunnerTest {
    private val tempDirs = mutableListOf<File>()

    @AfterTest
    fun cleanUpTempDirs() {
        tempDirs.forEach { it.deleteRecursively() }
    }

    private fun tempDir(): File {
        val dir = Files.createTempDirectory("reqnroll-run-test-runner-test").toFile()
        tempDirs += dir
        return dir
    }
    private fun target(
        declaringTypeFullName: String = "Tests.FFeature",
        methodName: String = "AddNumbers",
    ) = ScenarioTestTargetItem(declaringTypeFullName = declaringTypeFullName, methodName = methodName)

    // ── findOwningProjectPath ────────────────────────────────────────────────

    @Test
    fun `finds the project whose folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj", "/repo/Bar/Bar.csproj")
        assertEquals("/repo/Foo/Foo.csproj", RunTestRunner.findOwningProjectPath("/repo/Foo/Features/A.feature", projects))
    }

    @Test
    fun `deepest matching folder wins for a nested project`() {
        val projects = listOf("/repo/Foo.csproj", "/repo/Foo/Nested/Nested.csproj")
        assertEquals(
            "/repo/Foo/Nested/Nested.csproj",
            RunTestRunner.findOwningProjectPath("/repo/Foo/Nested/A.feature", projects),
        )
    }

    @Test
    fun `returns null when no project folder contains the file`() {
        val projects = listOf("/repo/Foo/Foo.csproj")
        assertNull(RunTestRunner.findOwningProjectPath("/elsewhere/A.feature", projects))
    }

    @Test
    fun `returns null for an empty project list`() {
        assertNull(RunTestRunner.findOwningProjectPath("/repo/Foo/A.feature", emptyList()))
    }

    // ── normalizeToManagedAssemblyPath ───────────────────────────────────────

    @Test
    fun `normalizeToManagedAssemblyPath returns a dll path unchanged`() {
        val path = "/repo/bin/Debug/net8.0/Tests.dll"
        assertEquals(path, RunTestRunner.normalizeToManagedAssemblyPath(path))
    }

    @Test
    fun `normalizeToManagedAssemblyPath resolves an apphost binary to its dll sibling when it exists`() {
        // Reproduces issue #722 follow-up: an MTP-mode project's exePath points at the native
        // apphost (no extension on Linux/macOS), not the managed .dll the reporter actually reports.
        val root = tempDir()
        val exePath = File(root, "Tests").path
        File(root, "Tests.dll").writeText("not a real assembly, just needs to exist")

        assertEquals(File(root, "Tests.dll").path, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    @Test
    fun `normalizeToManagedAssemblyPath resolves a Windows apphost exe to its dll sibling`() {
        val root = tempDir()
        val exePath = File(root, "Tests.exe").path
        File(root, "Tests.dll").writeText("not a real assembly, just needs to exist")

        assertEquals(File(root, "Tests.dll").path, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    @Test
    fun `normalizeToManagedAssemblyPath falls back to the original path when no dll sibling exists`() {
        val root = tempDir()
        val exePath = File(root, "Tests").path

        assertEquals(exePath, RunTestRunner.normalizeToManagedAssemblyPath(exePath))
    }

    // ── buildTestFilter ──────────────────────────────────────────────────────

    @Test
    fun `a single target produces one filter term`() {
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(listOf(target())))
    }

    @Test
    fun `row-tests targets sharing one method collapse to a single term`() {
        val targets = listOf(target(), target(), target())
        assertEquals("FullyQualifiedName=Tests.FFeature.AddNumbers", RunTestRunner.buildTestFilter(targets))
    }

    @Test
    fun `individual-methods targets with distinct method names each get their own term`() {
        val targets = listOf(
            target(methodName = "CheckValue_1"),
            target(methodName = "CheckValue_2"),
        )
        assertEquals(
            "FullyQualifiedName=Tests.FFeature.CheckValue_1|FullyQualifiedName=Tests.FFeature.CheckValue_2",
            RunTestRunner.buildTestFilter(targets),
        )
    }

    @Test
    fun `an empty target list produces an empty filter`() {
        assertEquals("", RunTestRunner.buildTestFilter(emptyList()))
    }

    // ── buildLoggerArgument ──────────────────────────────────────────────────

    @Test
    fun `buildLoggerArgument matches the friendly name and parameter keys VS injects into runsettings`() {
        val registration = RegisterTestRunResponse(success = true, runId = "run-1", endpoint = "127.0.0.1:5000")

        assertEquals(
            "ReqnrollIde;Endpoint=127.0.0.1:5000;RunId=run-1;IdeProcessId=4242",
            RunTestRunner.buildLoggerArgument(registration, ideProcessId = 4242L),
        )
    }

    // ── combineServerOutcomes ────────────────────────────────────────────────

    private fun outcome(aggregate: String, vararg rows: TestOutcomeRowItem) =
        GetTestOutcomeResponse(found = true, aggregate = aggregate, rows = rows.toList())

    private fun row(displayName: String, outcome: String, failedStepText: String? = null) =
        TestOutcomeRowItem(displayName = displayName, outcome = outcome, failedStepText = failedStepText)

    @Test
    fun `combineServerOutcomes is Passed when every method's aggregate passed`() {
        val result = RunTestRunner.combineServerOutcomes(listOf(outcome("Passed", row("r1", "Passed"))))
        assertEquals(RunOutcome.PASSED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes is Failed if any method's aggregate failed, even when others passed`() {
        val result = RunTestRunner.combineServerOutcomes(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result.outcome)
    }

    @Test
    fun `combineServerOutcomes flattens rows across every method and carries the failed-step text`() {
        val result = RunTestRunner.combineServerOutcomes(
            listOf(
                outcome("Passed", row("row 1", "Passed")),
                outcome("Failed", row("row 2", "Failed", "When the calculation explodes")),
            ),
        )
        assertEquals(2, result.rows.size)
        assertEquals(RunResultRow("row 1", RunOutcome.PASSED, null), result.rows[0])
        assertEquals(RunResultRow("row 2", RunOutcome.FAILED, "When the calculation explodes"), result.rows[1])
    }

    // ── combineIfComplete ────────────────────────────────────────────────────

    @Test
    fun `combineIfComplete combines when every method has a fresh, settled outcome`() {
        val result = RunTestRunner.combineIfComplete(
            listOf(outcome("Passed", row("r1", "Passed")), outcome("Failed", row("r2", "Failed"))),
        )
        assertEquals(RunOutcome.FAILED, result?.outcome)
        assertEquals(2, result?.rows?.size)
    }

    @Test
    fun `combineIfComplete is null when any method's lookup failed or was not found`() {
        assertNull(RunTestRunner.combineIfComplete(listOf(outcome("Passed", row("r1", "Passed")), null)))
        assertNull(
            RunTestRunner.combineIfComplete(
                listOf(outcome("Passed", row("r1", "Passed")), GetTestOutcomeResponse(found = false)),
            ),
        )
    }

    @Test
    fun `combineIfComplete rejects a previous run's stale outcome instead of reporting it as this run's`() {
        val stale = outcome("Failed", row("r1", "Failed")).copy(isStale = true)
        assertNull(RunTestRunner.combineIfComplete(listOf(stale)))
    }

    @Test
    fun `combineIfComplete keeps waiting while a method is still marked running`() {
        val running = outcome("Running", row("r1", "Running")).copy(isRunning = true)
        assertNull(RunTestRunner.combineIfComplete(listOf(outcome("Passed", row("r0", "Passed")), running)))
    }

    @Test
    fun `combineIfComplete is null for no methods at all`() {
        assertNull(RunTestRunner.combineIfComplete(emptyList()))
    }

    // ── looksLikeMtpProject ──────────────────────────────────────────────────

    private fun projectFile(root: File, xml: String, relativePath: String = "Proj/Proj.csproj"): File {
        val file = File(root, relativePath)
        file.parentFile.mkdirs()
        file.writeText(xml)
        return file
    }

    @Test
    fun `looksLikeMtpProject is true when the project itself sets EnableMSTestRunner and TestingPlatformDotnetTestSupport`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertTrue(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is true when the project sets EnableNUnitRunner and dotnet test opts into native MTP mode via global json`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>")
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertTrue(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is true when only MTP-capable, with no evidence dotnet test actually redirects to it`() {
        // Corrected (issue #722 follow-up) from the original plan §7 risk #5 assumption that
        // capability alone is harmless without a redirect/native signal — live-verified false: a
        // project with IsTestingPlatformApplication=true and neither TestingPlatformDotnetTestSupport
        // nor a native global.json opt-in set still has *every* dotnet test invocation hard-rejected
        // by Microsoft.Testing.Platform.MSBuild.targets on the .NET 10 SDK ("Testing with VSTest
        // target is no longer supported..."), --logger or not. looksLikeMtpProject only answers "was
        // a missing TRX our fault", so capability alone is exactly the right (broader) signal for it
        // — detectDotnetTestMode's own VS_TEST-when-no-redirect choice is unaffected by this: it's a
        // separate question (which command-line shape to *attempt*), and it stays covered by the
        // other detectDotnetTestMode tests above.
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup></Project>",
        )

        assertTrue(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is false when only the dotnet test redirect is set, with no MTP-capable framework`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>")

        assertFalse(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is false for a plain VSTest project with no Directory Build props`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")

        assertFalse(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is false when EnableMSTestRunner is explicitly false`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>false</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertFalse(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject finds a repo-root Directory Build props the project itself doesn't set`() {
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>" +
                "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>"
        )
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertTrue(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject combines a capability signal from one file with a redirect signal from another`() {
        // EnableMSTestRunner in the project itself, TestingPlatformDotnetTestSupport only in the
        // repo-root Directory.Build.props — both must be honored even though they come from
        // different files along the ancestor chain.
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>"
        )
        val project = projectFile(root, "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertTrue(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject does not climb above the git root`() {
        val outside = tempDir()
        File(outside, "Directory.Build.props").writeText(
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>"
        )
        val repo = File(outside, "repo")
        File(repo, ".git").mkdirs()
        val project = projectFile(repo, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertFalse(RunTestRunner.looksLikeMtpProject(project.path) { null })
    }

    @Test
    fun `looksLikeMtpProject is false for a project file that does not exist and no props anywhere`() {
        val root = tempDir()

        assertFalse(RunTestRunner.looksLikeMtpProject(File(root, "Missing/Missing.csproj").path) { null })
    }

    // ── detectDotnetTestMode ─────────────────────────────────────────────────

    @Test
    fun `detectDotnetTestMode is VS_TEST for a plain project`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")

        assertEquals(DotnetTestMode.VS_TEST, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    @Test
    fun `detectDotnetTestMode is VS_TEST when MTP-capable but nothing redirects dotnet test to it`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup></Project>")

        assertEquals(DotnetTestMode.VS_TEST, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    @Test
    fun `detectDotnetTestMode is MTP_COMPAT when TestingPlatformDotnetTestSupport is set and no native mode`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        assertEquals(DotnetTestMode.MTP_COMPAT, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    @Test
    fun `detectDotnetTestMode is MTP_NATIVE when global json opts into native mode, even with TestingPlatformDotnetTestSupport also set`() {
        // Plan §5.7's state machine: NativeDotnetTestModeActive takes precedence over the compat
        // redirect when both are somehow present.
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertEquals(DotnetTestMode.MTP_NATIVE, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    @Test
    fun `detectDotnetTestMode is MTP_NATIVE from a global json found only by climbing to the repo root`() {
        val root = tempDir()
        File(root, ".git").mkdirs()
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")
        val project = projectFile(root, "<Project><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>", "src/Tests/Tests.csproj")

        assertEquals(DotnetTestMode.MTP_NATIVE, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    @Test
    fun `detectDotnetTestMode is VS_TEST when global json opts into native mode but the project is not MTP-capable`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>")
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")

        assertEquals(DotnetTestMode.VS_TEST, RunTestRunner.detectDotnetTestMode(project.path) { null })
    }

    // ── detectDotnetTestMode: MSBuild-evaluated signal (issue #722) ─────────────

    @Test
    fun `detectDotnetTestMode trusts a true MSBuild evaluation even when the project file text says nothing`() {
        // Reproduces issue #722: a project made MTP-capable only through an imported props file
        // (e.g. referencing the full xunit.v3 runner package) has none of the marker properties as
        // literal text anywhere a scan would look, but a real MSBuild evaluation sees it.
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = RunTestRunner.detectDotnetTestMode(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = true)
        }

        assertEquals(DotnetTestMode.MTP_COMPAT, mode)
    }

    @Test
    fun `detectDotnetTestMode is VS_TEST when the MSBuild evaluation says mtpCapable but nothing redirects`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = RunTestRunner.detectDotnetTestMode(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.VS_TEST, mode)
    }

    @Test
    fun `detectDotnetTestMode combines a true MSBuild evaluation with a native global json opt-in`() {
        val root = tempDir()
        File(root, "global.json").writeText("""{ "test": { "runner": "Microsoft.Testing.Platform" } }""")
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val mode = RunTestRunner.detectDotnetTestMode(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.MTP_NATIVE, mode)
    }

    @Test
    fun `detectDotnetTestMode falls back to the text scan when the MSBuild evaluation is unavailable`() {
        val root = tempDir()
        val project = projectFile(
            root,
            "<Project><PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner>" +
                "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport></PropertyGroup></Project>",
        )

        val mode = RunTestRunner.detectDotnetTestMode(project.path) { null }

        assertEquals(DotnetTestMode.MTP_COMPAT, mode)
    }

    @Test
    fun `detectDotnetTestMode does not let a Directory Build props text match override a false MSBuild evaluation`() {
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

        val mode = RunTestRunner.detectDotnetTestMode(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = false, dotnetTestRedirectsToMtp = false)
        }

        assertEquals(DotnetTestMode.VS_TEST, mode)
    }

    // ── isMtpCapable / looksLikeMtpProject: MSBuild-evaluated signal ─────────

    @Test
    fun `isMtpCapable is true from a true MSBuild evaluation even without a redirect signal`() {
        // The exact shape live-verified against a real project (IsTestingPlatformApplication=true,
        // TestingPlatformDotnetTestSupport unset): detectDotnetTestMode correctly still picks
        // VS_TEST as the command-line shape to attempt, but isMtpCapable must be true so a resulting
        // "no TRX" reads as Inconclusive, not Failure.
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val capable = RunTestRunner.isMtpCapable(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = true, dotnetTestRedirectsToMtp = false)
        }

        assertTrue(capable)
    }

    @Test
    fun `isMtpCapable is false from a false MSBuild evaluation`() {
        val root = tempDir()
        val project = projectFile(root, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")

        val capable = RunTestRunner.isMtpCapable(project.path) {
            RunTestRunner.MtpEvaluation(mtpCapable = false, dotnetTestRedirectsToMtp = false)
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

        assertNull(RunTestRunner.evaluateMtpPropertiesViaMsbuild(missing))
    }
}
