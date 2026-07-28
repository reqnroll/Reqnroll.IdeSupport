import org.gradle.internal.os.OperatingSystem
import org.jetbrains.intellij.platform.gradle.IntelliJPlatformType
import org.jetbrains.intellij.platform.gradle.models.ProductRelease

plugins {
    kotlin("jvm") version "2.0.21"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

group = providers.gradleProperty("pluginGroup").get()
version = providers.gradleProperty("pluginVersion").get()

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        rider(providers.gradleProperty("platformVersion"))
        instrumentationTools()
    }

    // Plain JUnit5 (kotlin.test assertions on the JUnit5 engine) for pure-logic unit tests that
    // don't need an IntelliJ Platform fixture — see src/test/kotlin. Platform-fixture tests
    // (BasePlatformTestCase) would need intellijPlatform { testFramework(TestFrameworkType.Platform) }
    // instead/additionally; not needed yet since nothing under test touches the platform directly.
    testImplementation(kotlin("test-junit5"))
}

kotlin {
    jvmToolchain(21)
}

tasks.test {
    useJUnitPlatform()
}

intellijPlatform {
    pluginConfiguration {
        id = providers.gradleProperty("pluginId")
        name = "Reqnroll"
        version = providers.gradleProperty("pluginVersion")

        ideaVersion {
            sinceBuild = providers.gradleProperty("pluginSinceBuild")
            untilBuild = providers.gradleProperty("pluginUntilBuild")
        }
    }

    // `recommended()` picks a couple of IDE versions from JetBrains' own heuristic -- without
    // this block, `verifyPlugin` fails immediately with "No IDE resolved for verification".
    // `select` adds every RELEASE-channel Rider build across the declared 243-251.* compatibility
    // range (see issue #271) -- in practice this resolves to just the earliest and latest builds
    // published on that channel in range (confirmed live: RD-243.28141.39/RD-251.29188.32), which
    // is exactly the "spread across the declared range" the issue asked for. Unlike hardcoding
    // specific build numbers, this stays correct as new patch releases land without needing manual
    // upkeep, and RELEASE-only avoids pulling in every EAP/nightly, which would multiply this
    // already-slow CI job (#149) for little benefit. Every API this plugin depends on
    // (LspServerManager, LspServerDescriptor, LspServerSupportProvider) is @Experimental, so this
    // is a cheap, static, bytecode-level early warning if one of those APIs changes or disappears
    // somewhere in the declared range.
    pluginVerification {
        ides {
            recommended()
            select {
                types = listOf(IntelliJPlatformType.Rider)
                channels = listOf(ProductRelease.Channel.RELEASE)
                sinceBuild = providers.gradleProperty("pluginSinceBuild")
                untilBuild = providers.gradleProperty("pluginUntilBuild")
            }
        }
    }
}

// ── Bundle the Reqnroll.IdeSupport LSP server ───────────────────────────────
//
// ReqnrollServerPathResolver (src/main/kotlin/.../lsp/ReqnrollServerPathResolver.kt)
// expects server/<rid>/Reqnroll.IdeSupport.LSP.Server[.exe] under the plugin's own
// install directory, for whichever RID matches the OS Rider is actually running on
// — so a distributable build needs every supported RID bundled at once, mirroring
// the layout src/VSCode's build produces (a single, OS-detecting .vsix/.zip).
//
// Two ways to populate server/<rid>/ here, matching the `UseExternalLspServerBuild`
// / `LspServerBuildDir` MSBuild properties the VS extension build already uses for
// the same problem (see ci.yml's build-vs-extension job):
//
//  - Local dev (no -PlspServerBuildDir): publishServer runs `dotnet publish` for the
//    host RID only — fast, and sufficient since a local `runIde` only ever needs the
//    server for the OS it's running on.
//  - CI (-PlspServerBuildDir=<dir>): skips publishServer entirely and instead copies
//    whichever server-<rid> subdirectories already exist under <dir> — CI populates
//    those from the already-built-and-tested artifacts test-lsp.yml publishes, so
//    Gradle never needs `dotnet` on the CI runner at all.

val repoRoot = layout.projectDirectory.dir("../..").asFile.canonicalFile
val allServerRids = listOf("win-x64", "linux-x64", "osx-x64", "osx-arm64")

fun defaultServerRid(): String {
    val os = OperatingSystem.current()
    val arch = System.getProperty("os.arch").lowercase()
    return when {
        os.isWindows -> "win-x64"
        os.isMacOsX -> if (arch.contains("aarch64") || arch.contains("arm")) "osx-arm64" else "osx-x64"
        else -> "linux-x64"
    }
}

// Override with e.g. `./gradlew runIde -PserverRid=linux-x64` to publish/bundle a
// different single RID for local dev. Ignored once -PlspServerBuildDir is set.
val serverRid = (findProperty("serverRid") as String?) ?: defaultServerRid()
val serverOutputDir = layout.projectDirectory.dir("server/$serverRid")
val serverProject = File(repoRoot, "src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj")
val connectorProject = File(repoRoot, "src/LSP/Reqnroll.IdeSupport.LSP.Connector/Connector/Connector.csproj")

// `runIde` is the local dev-sandbox entry point — Gradle itself has no MSBuild-style
// Debug/Release build-type concept, so "Debug build of the Rider plugin" is interpreted as
// "invoked via runIde" (as opposed to buildPlugin/verifyPlugin/CI, which package for real
// distribution and should keep bundling the Release server). Checked via the requested task
// names rather than a project property so plain `./gradlew runIde` does the right thing with
// no extra flags.
val isRunIdeInvocation = gradle.startParameter.taskNames.any { it == "runIde" || it.endsWith(":runIde") }
val serverConfiguration = if (isRunIdeInvocation) "Debug" else "Release"

// Directory containing one server-<rid>-shaped subdirectory per RID, pre-published
// and tested by CI (see .github/workflows/ci.yml's build-rider-plugin job). When set,
// publishServer is skipped and prepareSandbox bundles every RID found here instead
// of just the host's.
val externalServerBuildDir = (findProperty("lspServerBuildDir") as String?)?.let { File(it) }

val publishServer by tasks.registering(Exec::class) {
    group = "reqnroll"
    description = "Publishes Reqnroll.IdeSupport.LSP.Server (RID=$serverRid, configuration=$serverConfiguration) " +
        "into server/$serverRid. Skipped when -PlspServerBuildDir is set."
    onlyIf { externalServerBuildDir == null }

    // Track the full source trees that feed the published server (not just the .csproj file
    // itself, which was the previous, too-narrow input — Gradle's up-to-date check would report
    // "up-to-date" and silently skip republishing whenever any .cs file changed without also
    // touching the .csproj, serving a stale binary to runIde just like VS Code's F5 did before
    // dev-publish-server.mjs, except less visibly since Gradle reported success). Covers both
    // src/LSP (Server + Connector + all their project references) and src/Core (Common, which
    // the Server references but isn't under src/LSP).
    inputs.files(
        fileTree(File(repoRoot, "src/LSP")) { exclude("**/bin/**", "**/obj/**") },
        fileTree(File(repoRoot, "src/Core")) { exclude("**/bin/**", "**/obj/**") },
    )
    // Also invalidate the up-to-date check when switching between runIde (Debug) and
    // buildPlugin/CI (Release) against otherwise-unchanged source, so alternating between the
    // two locally doesn't serve a stale build from the other configuration.
    inputs.property("serverConfiguration", serverConfiguration)
    outputs.dir(serverOutputDir)

    doFirst {
        // Restore the Connector project for this RID first — it's multi-TFM and doesn't
        // resolve correctly as part of the Server's own restore. Same requirement as
        // src/VSCode/scripts/publish-server.sh.
        // `project.exec` (not bare `exec`) — this task is itself an `Exec` task, which has its
        // own no-arg `exec(): Unit` member that shadows the `Project.exec(Action)` extension;
        // the unqualified name resolves to that member and fails to compile ("too many
        // arguments") under some Gradle/Kotlin-DSL combinations.
        project.exec {
            commandLine("dotnet", "restore", connectorProject.toString(), "--runtime", serverRid)
        }
    }

    commandLine(
        "dotnet", "publish", serverProject.toString(),
        "--configuration", serverConfiguration,
        "--runtime", serverRid,
        "--self-contained", "true",
        "--nologo",
        "--output", serverOutputDir.asFile.absolutePath,
    )
}

tasks.named<Sync>("prepareSandbox") {
    val externalDir = externalServerBuildDir
    if (externalDir == null) {
        dependsOn(publishServer)
        from(serverOutputDir) {
            into("${project.name}/server/$serverRid")
        }
    } else {
        allServerRids.forEach { rid ->
            val ridDir = File(externalDir, rid)
            if (ridDir.exists()) {
                from(ridDir) {
                    into("${project.name}/server/$rid")
                }
            }
        }
    }
}

tasks {
    wrapper {
        gradleVersion = "8.10"
    }
}

// Flags the sandboxed IDE process launched by `runIde` as a dev sandbox so
// ReqnrollLspServerDescriptor can default to verbose LSP server logging there, without
// affecting a real installed plugin (which never sets this system property). runIde is a
// JavaExec-derived task in the IntelliJ Platform Gradle Plugin, so `systemProperty` applies to
// the JVM it launches (the sandboxed IDE itself, not our LSP server subprocess — the plugin
// code reads it and passes the corresponding --log-level through when it spawns the server).
tasks.matching { it.name == "runIde" }.configureEach {
    (this as? JavaExec)?.systemProperty("reqnroll.devSandbox", "true")
}
