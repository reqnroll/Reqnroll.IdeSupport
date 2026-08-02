# Reqnroll.IdeSupport

LSP-based IDE support for [Reqnroll](https://reqnroll.net/), the open-source Gherkin/BDD test
framework for .NET. One shared LSP server drives Gherkin syntax highlighting, diagnostics,
navigation, completions, formatting, and refactoring across multiple IDEs, replacing the legacy
monolithic [`Reqnroll.VisualStudio`](https://github.com/reqnroll/Reqnroll.Visualstudio) VS
extension with a design that works the same way in Visual Studio, VS Code, and Rider.

> **Status:** Preview / active development. The Visual Studio, VS Code, and Rider clients are all
> functional and cover most of the planned feature set; none are yet published to their
> respective marketplaces. See
> [Open Questions & Risk Register](docs/LSP-IDE-Support-Open-Questions.md) for what's still
> unresolved and the [Issues](../../issues) tab for tracked defects and to-dos. Not yet promoted
> as the recommended replacement for the legacy extension — see
> [Overview §5](docs/LSP-IDE-Support-Overview.md#5-release-strategy-and-migration-plan) for the
> promotion criteria.

## Why this exists

The legacy `Reqnroll.VisualStudio` extension is a single VS-SDK codebase with no path to VS Code
or Rider. This project extracts the Gherkin-editing intelligence (parsing, diagnostics, step
matching, navigation, formatting) into a standalone [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
server, so each IDE only needs a thin client. See
[docs/LSP-IDE-Support-Overview.md](docs/LSP-IDE-Support-Overview.md) for the full goals,
non-goals, and phased roadmap.

## Repository layout

```
src/
  Core/
    Reqnroll.IdeSupport.Common                    ← shared config/logging/analytics contracts
    Reqnroll.IdeSupport.ReqnrollConnector.Models   ← DTOs shared with the out-of-proc connector
  LSP/
    Reqnroll.IdeSupport.LSP.Core                   ← Gherkin parser, binding registry, match cache (netstandard2.0)
    Reqnroll.IdeSupport.LSP.Server                 ← the LSP server itself (OmniSharp.Extensions.LanguageServer host)
    Reqnroll.IdeSupport.LSP.Connector              ← out-of-process reflection-based binding discovery, per-TFM
  VisualStudio/
    Reqnroll.IdeSupport.VisualStudio.Extension     ← VS.Extensibility LSP client (VSIX)
    Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration ← MEF classifications, analytics, VSSDK fallback
    Reqnroll.IdeSupport.VisualStudio.Wizards*      ← New Project / New Item wizards, welcome dialog
    Reqnroll.IdeSupport.VisualStudio.ItemTemplates,
    Reqnroll.IdeSupport.VisualStudio.ProjectTemplate ← VSIX template packaging
  VSCode/                                          ← TypeScript VS Code extension (npm project)
  Rider/                                           ← Kotlin IntelliJ Platform plugin (Gradle project)

tests/            ← unit tests, integration specs, and BDD spec projects mirroring src/
docs/             ← design docs (see below)
```

Each subproject's own README/CONTRIBUTING/XML doc comments explain its internals in more detail;
the [Architecture reference](docs/LSP-IDE-Support-Architecture.md) is the canonical map of how
everything fits together.

## Design documentation

| Document | Read it for… |
|---|---|
| [Overview](docs/LSP-IDE-Support-Overview.md) | Scope, goals/non-goals, high-level architecture diagram, phased roadmap, release/migration strategy |
| [Architecture & Implementation Reference](docs/LSP-IDE-Support-Architecture.md) | Module design, server internals (workspace model, membership index, pipeline), per-IDE client details, cross-cutting concerns |
| [Feature Designs](docs/LSP-IDE-Support-Feature-Designs.md) | Per-feature (F1–F20) design, sequence diagrams, as-built notes |
| [Open Questions & Risk Register](docs/LSP-IDE-Support-Open-Questions.md) | Active open questions and risks — check here before assuming something is decided |

`docs/Archive/` holds superseded or fully-implemented design/plan documents kept for historical
reference — each doc's own status banner says whether it's active or archived-and-why.

## End-user documentation

Drafted end-user documentation (installation, editing features, navigation features) lives in
[docs/site/ide-support/](docs/site/ide-support/), authored in MyST/Sphinx alongside the code that
implements the features it describes. It isn't published yet — see
[docs/site/README.md](docs/site/README.md) for the plan to sync it into
[reqnroll/Reqnroll](https://github.com/reqnroll/Reqnroll)'s `docs/ide-integrations/ide-support/`
and publish it to docs.reqnroll.net, and issue [#63](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/63)
for the content outline.

## Building

There's no single top-level build for every project in this repo (the VS extension is
net481/VSSDK, the LSP server is net10.0 cross-platform, the VS Code extension is a separate
npm project, and the Rider plugin is a separate Kotlin/Gradle project) — build the piece you're
working on:

**LSP Server** (net10.0, cross-platform):
```sh
dotnet build src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj
```

**Visual Studio Extension** (net481, requires Visual Studio + the Extensibility/VSSDK workloads):
```sh
dotnet build src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/Reqnroll.IdeSupport.VisualStudio.Extension.csproj
```
Building the Extension project also publishes the LSP server self-contained (win-x64) into the
VSIX under `LSPServer/` — rebuild the Extension after any server change to pick it up.

**VS Code Extension** (TypeScript):
```sh
cd src/VSCode
npm ci
npm run build:server   # publishes the LSP server for your host platform
npm run compile
```

**Rider Plugin** (Kotlin/IntelliJ Platform, requires a JDK 21 — see
[src/Rider/CONTRIBUTING.md](src/Rider/CONTRIBUTING.md) for the devcontainer alternative if you
can't install one locally):
```sh
cd src/Rider
./gradlew buildPlugin   # publishes the LSP server for the host RID, then produces build/distributions/*.zip
./gradlew runIde        # launches a sandboxed Rider instance with the plugin loaded
```
No local Rider install is required — the IntelliJ Platform Gradle plugin downloads the pinned
Rider SDK automatically on first build.

The .NET projects (LSP server, VS extension) can also be opened as one workspace via the
solution file [`Reqnroll.IdeSupport.slnx`](Reqnroll.IdeSupport.slnx) in an IDE that supports the
`.slnx` format (Visual Studio 2022 17.13+, VS Code with the C# Dev Kit). The VS Code extension
and Rider plugin are separate npm/Gradle projects and aren't part of that solution.

### Server logging and trace verbosity

The LSP server accepts three independent verbosity flags — `--log-level` (its own file logging),
`--protocol-log-level` (OmniSharp's internal diagnostics), and `--trace` (the LSP `$/logTrace`
protocol trace) — each defaulting to a quiet level when omitted. Each IDE's glue component sets
its own defaults for these (chattier in DEBUG builds); see
[src/LSP/CONTRIBUTING.md](src/LSP/CONTRIBUTING.md#server-logging-and-trace-verbosity) for the full
flag reference and [src/VisualStudio/CONTRIBUTING.md](src/VisualStudio/CONTRIBUTING.md) /
[src/VSCode/CONTRIBUTING.md](src/VSCode/CONTRIBUTING.md) for how each client wires them up.

See [CONTRIBUTING.md](CONTRIBUTING.md) and the area-specific contributor guides
([LSP Server](src/LSP/CONTRIBUTING.md), [Visual Studio extension](src/VisualStudio/CONTRIBUTING.md),
[VS Code extension](src/VSCode/CONTRIBUTING.md), [Rider plugin](src/Rider/CONTRIBUTING.md)) for
full development workflows, debugging, and test instructions.

## Testing

```sh
# LSP server unit tests
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj

# LSP server integration specs (Reqnroll .feature BDD, server hosted in-process)
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Reqnroll.IdeSupport.LSP.Server.Specs.csproj

# VS extension client-side unit tests
dotnet test tests/VisualStudio/Reqnroll.VisualStudio.Tests/Reqnroll.VisualStudio.Tests.csproj

# VS Code extension tests (grammar + utility functions, no VS Code required)
cd tests/VSCode && npm ci && npm test

# Rider plugin tests (Kotlin, JUnit via Gradle)
cd src/Rider && ./gradlew test
```

CI (`.github/workflows/ci.yml`, with LSP server tests split out into
`.github/workflows/test-lsp.yml`) is driven by a path-filter job, so it only builds/tests the
clients whose paths actually changed — a Rider-only Kotlin change, for example, doesn't also
rebuild the VS Code extension. On qualifying pushes/PRs it builds and tests the VS Code extension
and the Rider plugin, and publishes the LSP server for all supported runtimes (win-x64, linux-x64,
osx-x64, osx-arm64). See the comment header at the top of `ci.yml` for the full job dependency
graph.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[BSD 3-Clause](LICENSE.txt).
