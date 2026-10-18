# Reqnroll IDE Support: Overview

**Status:** Implemented in Preview. Describes the system as it is; detail lives in the documents linked below.
**Audience:** Anyone new to the repository, and maintainers deciding where to look.

## 1. Purpose and scope

Reqnroll IDE Support provides Gherkin editing intelligence for [Reqnroll](https://reqnroll.net/) projects in
**Visual Studio 2022/2026, VS Code and JetBrains Rider** from one shared language server. It targets Reqnroll
only, and uses the `OmniSharp.Extensions.LanguageServer` package as its LSP framework. Where a feature can be
delivered through a generic LSP capability it is; where an IDE needs plugin code, the design documents say so.

**Goals**

- Parity with the legacy extensions (`Reqnroll.VisualStudio` and the legacy Rider plugin, `Reqnroll.Rider`),
  in all three IDEs.
- Every feature backed by automated tests, including measured latency against defined targets (see the
  performance verification section of the [Performance](architecture/performance.md)).
- A "Preview" release alongside the legacy extension during the transition.

**Non-goals**

- SpecFlow or plain Cucumber compatibility.
- Step definitions written in F#; VB.NET templates.
- Migrating the legacy extensions (each continues unchanged until its replacement reaches parity).

## 2. Architecture at a glance

```mermaid
graph TB
    subgraph Editors["IDE Clients"]
        VSCode["VS Code Extension<br/>(TypeScript / vscode-languageclient)"]
        VS["Visual Studio Extension<br/>(VS.Extensibility + VSSDK)"]
        Rider["Rider Plugin<br/>(Kotlin)"]
    end

    subgraph Server["LSP Server (net10.0, cross-platform executable)"]
        Handlers["LSP handlers<br/>(OmniSharp.Extensions.LanguageServer)"]
        subgraph Core["LSP.Core (netstandard2.0)"]
            Parser["Gherkin parser and tag builder"]
            Buffer["Document buffer<br/>(tag tree cache)"]
            Registry["Binding registry"]
            Match["Binding match service"]
            Features["Diagnostics, completion, formatting,<br/>symbols, inlay hints, test targets, outcomes"]
        end
        Roslyn["Roslyn discovery<br/>(source analysis)"]
    end

    Connector["Binding connector<br/>(out-of-process, reflection)"]
    Common["Common (netstandard2.0)<br/>configuration, logging, telemetry"]

    VS & VSCode & Rider -->|"JSON-RPC over stdio"| Handlers
    Handlers --> Parser & Buffer & Match & Features
    Roslyn --> Registry
    Match --> Registry
    Connector -.->|"discovery result on stdout"| Registry
    Server -.->|"launches per request"| Connector
    Core --> Common
```

Each client is a thin shell: it launches the server, forwards editor and project-system events, and renders
results with the IDE's own UI. Everything language-specific is in the server. The server is client-agnostic
apart from a small number of per-IDE capability decisions made at `initialize`, driven by the `--ide` flag.

## 3. Flows

### 3.1 Editing a feature file

```mermaid
sequenceDiagram
    actor U as User
    participant C as IDE client
    participant S as Sync handler
    participant P as IdeSupportTagParser
    participant B as Document buffer + match service
    participant D as Diagnostics aggregator

    U->>C: edit .feature
    C->>S: textDocument/didChange
    S->>P: parse whole file, match steps (one pass)
    P-->>B: tag tree + match set (written together)
    S-->>C: response (does not wait for diagnostics)
    B-->>D: MatchCacheChangedNotification
    D-->>C: textDocument/publishDiagnostics
    C->>S: later pulls (semantic tokens, outline, folding...)
    S-->>C: answers from the cached tag tree
```

### 3.2 Bindings change (C# edit or build)

```mermaid
sequenceDiagram
    participant C as IDE client
    participant S as LSP server
    participant R as Roslyn discovery
    participant K as Connector (out of process)
    participant G as Binding registry
    participant H as BindingRegistryChangedHandler
    participant B as Document buffer + match service

    alt .cs file edited
        C->>S: didOpen / didChange on .cs
        S->>R: re-analyze that file
        R->>G: replace that file's bindings
    else build finished
        C->>S: workspace/didChangeWatchedFiles (output assembly)
        S->>K: launch, scan compiled assembly
        K-->>S: discovery result (stdout)
        S->>G: replace the full registry
    end
    G-->>H: registry changed
    H->>B: re-parse each open feature against new registry
    B-->>C: diagnostics and semantic tokens refreshed
```

## 4. Glossary

| Term | Meaning here |
|---|---|
| **Binding** | A C# method with a Reqnroll step or hook attribute (`[Given]`, `[BeforeScenario]`, …). |
| **Step definition** | A binding for a step (as opposed to a hook). |
| **Binding registry** | The server's per-project store of discovered bindings. |
| **Match set** | The result of matching a feature's steps against the registry. |
| **Tag tree** | `IdeSupportTag[]`: the parsed file annotated with structure and match results; the basis of nearly every feature. |
| **Roslyn discovery** | Source-level binding discovery from `.cs` text: immediate, no build required. |
| **Reflection discovery / connector** | Binding discovery from the compiled assembly by an out-of-process helper: authoritative, needs a build. |
| **Project membership index** | `path → {projects}` map that decides which project's registry a file belongs to, including linked and excluded files. |
| **`reqnroll/*` methods** | Custom LSP requests and notifications for things LSP has no vocabulary for (project information, test targets, outcomes). |
| **`--ide` flag** | Server argument naming the client; drives static versus dynamic capability registration. |
| **Preview** | The release designation of the new extensions while the legacy ones remain current. |

## 5. Key components

| Component | Location | Responsibility |
|---|---|---|
| `Reqnroll.IdeSupport.LSP.Server` | `src/LSP` | The executable: handler registration, hosting, per-IDE capability decisions, Roslyn discovery, connector client. |
| `Reqnroll.IdeSupport.LSP.Core` | `src/LSP` | Parser, tag tree, registry, matching, and the feature services (diagnostics, completion, formatting, symbols, inlay hints, test targets, test outcomes). |
| `Reqnroll.IdeSupport.LSP.Connector` | `src/LSP` | Out-of-process reflection scan producing the discovery result. |
| `Reqnroll.IdeSupport.Common` | `src/Core` | Configuration loading, logging, telemetry contracts, shared custom method names. |
| Visual Studio extension | `src/VisualStudio` | VS.Extensibility language client plus VSSDK integration (classifiers, CodeLens bridge, navigation bar, wizards, templates). |
| VS Code extension | `src/VSCode` | `vscode-languageclient` shell, commands, project evaluation. |
| Rider plugin | `src/Rider` | Kotlin plugin: generic LSP client with Rider-specific code vision and actions. |

## 6. Features

Each feature has a design in [Feature Designs](features/README.md).

| ID | Feature | ID | Feature |
|---|---|---|---|
| F1 | [Syntax highlighting](features/syntax-highlighting.md) | F15 | [Find unused step definitions](features/step-navigation.md) |
| F2 | [Binding discovery](architecture/binding-discovery.md) | F16 | [Step rename](features/rename.md) |
| F3 | [File diagnostics](features/diagnostics.md) | F17 | [Hook navigation](features/hooks-and-lenses.md) |
| F4 | [Parse error display](features/diagnostics.md) | F18 | [CodeLens: step usage counts](features/hooks-and-lenses.md) |
| F5 | [Go to step definition](features/step-navigation.md) | F19 | [New project / item wizards](Onboarding-And-Scaffolding.md) |
| F6 | [Define steps (scaffolding)](features/define-steps.md) | F20 | [Installation and upgrade](Onboarding-And-Scaffolding.md) |
| F7 | [Keyword completion](features/completion.md) | F23 | [Inlay hints](features/inlay-hints.md) |
| F8 | [Step completion](features/completion.md) | F24 | [Hook match CodeLens](features/hooks-and-lenses.md) |
| F9 | [Document outline](features/outline-and-folding.md) | F25 | [Hook match count CodeLens](features/hooks-and-lenses.md) |
| F10 | [Code folding](features/outline-and-folding.md) | F26 | [Test runner integration](Test-Runner-Integration-Design.md) |
| F11 | [Document formatting](features/formatting-and-comments.md) | F27 | [C# binding validation diagnostics](features/diagnostics.md) |
| F12 | [Table formatting](features/formatting-and-comments.md) | F28 | [Clickable tags](features/clickable-tags.md) |
| F13 | [Comment / uncomment](features/formatting-and-comments.md) | | |
| F14 | [Find step usages](features/step-navigation.md) | | |

F21 and F22 are not used. Test-runner and outcome flows are detailed in
[Test-Runner-Integration-Design.md](Test-Runner-Integration-Design.md) and
[Test-Outcomes-Pipeline.md](Test-Outcomes-Pipeline.md).

## 7. Distribution and the legacy extensions

**Naming.** The extensions are published as "Reqnroll Extension for {IDE} (Preview)" on the Visual Studio,
VS Code and JetBrains marketplaces, each under its own identifier.

**Coexistence.** The legacy `Reqnroll.VisualStudio` extension and the legacy Rider plugin (`Reqnroll.Rider`) each
continue unchanged throughout Preview, and the new Visual Studio extension and the new Rider plugin will each
eventually replace them. In Visual Studio both can be installed at once (different GUIDs, no shared in-process
components). There is no automatic migration. VS Code has no earlier Reqnroll extension to replace. How the two
Rider plugins coexist is not yet documented (see open question O9).

**Settings.** Existing `reqnroll.json` files work unchanged. Settings specific to the legacy extension are not
migrated; users reconfigure through the IDE's own settings.

**Promotion criteria.** The Preview label is lifted, and a new extension recommended over its legacy counterpart,
when: feature parity with the legacy extension is reached (F1–F20 plus inlay hints); the end-to-end suite passes
on the supported IDE versions; and the core team has used it for at least one release cycle. After promotion the
legacy extension is marked deprecated, points users at the new one, receives critical fixes for one more release
cycle, then goes to maintenance only. These criteria were written with Visual Studio in mind; whether they apply
unchanged to Rider is part of O9.

**Client differences to be aware of.** Some features are richer in some clients:

- *Rename change annotations* (grouped, labelled rename preview): VS Code and Rider negotiate the LSP 3.16
  `changeAnnotationSupport` capability. Visual Studio does not advertise it and gets the legacy edit shape, so
  renames apply silently.
- *Inlay hints* are a standard pull feature all three clients support. Visual Studio additionally needs the user
  to enable inline hints in Tools → Options → Text Editor; the extension does not force that setting.

## 8. Where to go next

| To learn about | Read |
|---|---|
| Module design, server internals, each IDE client, cross-cutting concerns | [Architecture & Implementation Reference](LSP-IDE-Support-Architecture.md) |
| How a specific feature works | [Feature Designs](features/README.md) |
| Custom `reqnroll/*` methods | [LSP-Protocol-Extensions.md](LSP-Protocol-Extensions.md) |
| What is open, accepted or settled | [Open Questions register](LSP-IDE-Support-Open-Questions.md) |
