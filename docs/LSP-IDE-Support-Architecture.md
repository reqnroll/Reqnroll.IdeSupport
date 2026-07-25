# Reqnroll LSP-Based IDE Support — Architecture & Implementation Reference

> **Status:** Draft for team review  
> **Audience:** Core team contributors

**Related documents**

| Document | Contents |
|----------|----------|
| [Overview](LSP-IDE-Support-Overview.md) | Scope, goals, high-level architecture, roadmap (F-number → feature name index), release strategy |
| [Feature Designs](LSP-IDE-Support-Feature-Designs.md) | Per-feature design, sequence diagrams, as-built notes (Appendix A / B) |
| [Open Questions & Risk Register](LSP-IDE-Support-Open-Questions.md) | Active open questions, risk register |

---

## Table of Contents

1. [LSP Concepts Primer](#1-lsp-concepts-primer)
2. [Where This Implementation Diverges from Standard LSP](#2-where-this-implementation-diverges-from-standard-lsp)
3. [Module Architecture](#3-module-architecture)
4. [Repository Structure](#4-repository-structure)
5. [LSP Server Design](#5-lsp-server-design)
6. [IDE Clients](#6-ide-clients)
7. [Binding Connector](#7-binding-connector)
8. [Testing Strategy](#8-testing-strategy)
9. [Cross-Cutting Concerns](#9-cross-cutting-concerns)
    - [Performance Requirements](#performance-requirements)
    - [Telemetry](#telemetry)
    - [Configuration](#configuration)
    - [Security](#security)
    - [CI/CD Pipeline](#cicd-pipeline)
    - [Versioning and Compatibility](#versioning-and-compatibility)
    - [LSP Message Tracing](#lsp-message-tracing)
    - [Error Handling and Resilience](#error-handling-and-resilience)
    - [End-User Troubleshooting and Logging](#end-user-troubleshooting-and-logging)
    - [Server Lifecycle](#server-lifecycle)
10. [Alternatives Considered](#10-alternatives-considered)
11. [Non-Feature Engineering Tasks](#11-non-feature-engineering-tasks)

---

## 1. LSP Concepts Primer

LSP (Language Server Protocol) decouples language intelligence from the editor. A **language server** runs as a separate process and communicates with any compliant **IDE client** over JSON-RPC 2.0. The same server binary serves all three IDEs in this project. What follows is the minimum background needed to read the rest of this document; the authoritative reference is the [LSP 3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/).

**Initialize / initialized handshake.** The client sends `initialize`, advertising its capabilities (what message types it understands, which optional features it supports). The server responds with its own capabilities. This negotiation determines the active feature set for the session — the server must not send messages for capabilities the client did not advertise. Once the client receives the response it sends `initialized` (a notification) to signal readiness; the server may then begin pushing workspace-wide notifications.

**Requests vs. notifications.** A *request* (`textDocument/completion`, `textDocument/definition`, …) expects a response. A *notification* (`textDocument/didChange`, `textDocument/publishDiagnostics`, …) does not. Handlers must never send a response to a notification. OmniSharp enforces this distinction through separate base classes.

**Document sync.** The client sends `textDocument/didOpen`, `textDocument/didChange`, and `textDocument/didClose` to keep the server's view of open files current. `didChange` may carry either the full new text or an incremental delta (depending on the `TextDocumentSyncKind` the server declared). This server declares `Incremental` but re-parses the full file on every change — see [§5 Document Scope](#document-scope) for the rationale.

**Push vs. pull.** Server-to-client data flows in one of two patterns:
- *Push* — the server sends proactively when its internal state changes (e.g. `textDocument/publishDiagnostics` after a binding registry update). The client cannot predict when this arrives.
- *Pull* — the client requests on demand (e.g. `textDocument/semanticTokens/full` when it wants to repaint). The server responds only when asked.

Most LSP capabilities use pull. This implementation adds a push path for Visual Studio semantic tokens (see [§2](#2-where-this-implementation-diverges-from-standard-lsp)).

**Semantic tokens.** The server encodes all document color information as a flat array of 5-integer tuples: `[deltaLine, deltaStartChar, length, tokenTypeIndex, tokenModifiersBitmask]`. Positions are *delta-encoded* relative to the previous token (not absolute). The `tokenTypeIndex` is an index into a `legend` the server declares in the `initialize` response. The legend is the contract between server and clients — reordering or removing entries is a breaking change.

**Static vs. dynamic capability registration.** A capability can be declared *statically* in the `initialize` response (the client knows about it immediately) or registered *dynamically* at runtime via `client/registerCapability` (useful when registration depends on workspace content). Dynamic registration is more flexible but not all clients handle it reliably for all capabilities; Visual Studio is a known case where static registration is required for semantic tokens.

**Custom notifications and requests.** LSP allows non-standard methods; by convention they are namespaced (e.g. `reqnroll/projectLoaded`). Clients that do not recognise a custom notification silently ignore it — this is how the `reqnroll/*` project-system notifications degrade gracefully on clients that do not yet consume them.

**Authoritative sources:**
- [LSP 3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)
- [LSP overview — Microsoft Learn](https://learn.microsoft.com/en-us/visualstudio/extensibility/language-server-protocol)
- [OmniSharp.Extensions.LanguageServer (GitHub)](https://github.com/OmniSharp/csharp-language-server-protocol)

---

## 2. Where This Implementation Diverges from Standard LSP

Several aspects of this implementation deviate from what a textbook LSP server does. Each deviation is driven by a concrete constraint — IDE quirk, project-system limitation, or performance requirement. This section collects them in one place so a new contributor is not surprised to find the code doing something unusual.

### Semantic token delivery for Visual Studio: server push + client classifier

A standard LSP client *pulls* semantic tokens by sending `textDocument/semanticTokens/full` to the server and rendering the response. Visual Studio breaks this in two independent ways (both confirmed empirically — see R1/R1a in the [Risk Register](LSP-IDE-Support-Open-Questions.md#risk-register)):

1. VS's built-in semantic-token colorizer maps token-type names through a fixed internal `switch` that recognises only standard LSP types and a small set of C++/Roslyn/Razor names. Every unrecognised name — including all `reqnroll.*` names — falls through to plain `"text"`. Registering same-named `ClassificationTypeDefinition` entries in the MEF registry is **not** consulted; the mapping is hardcoded.
2. VS pulls `semanticTokens/full` lazily and inconsistently via its own tagger lifecycle; in practice it sometimes never requests tokens for an open document.

The workaround is a **server-push + client-classifier** path that bypasses VS's native token pull entirely. When launched with `--client visualstudio`, the server pushes encoded tokens to the VS client via a custom `reqnroll/semanticTokens` notification every time step binding matches change [ `MatchCacheChangedNotification`]. The VS extension captures this notification, decodes the token data, and caches it in a process-wide `SemanticTokenClassificationStore`. A classic MEF `IClassifierProvider` / `GherkinSemanticClassifier` then reads those cached tokens and emits `ClassificationSpan`s using the existing `DeveroomClassifications` entries — the same classification names the existing VS extension uses, so users see no change in behavior of coloring (compared to the existing extension). VS Code and Rider are unaffected and use the standard pull flow.

The `--client` flag is the only place where the server behaves differently per IDE at the protocol level; the rest of the server is client-agnostic.

Full detail is in [F1 · Client-side token-type mapping](LSP-IDE-Support-Feature-Designs.md#client-side-token-type-mapping).

### Sync-first, async-rest internal pipeline

A typical LSP server dispatches an incoming message, calls services sequentially, and returns a response. This server uses a **sync-first, async-rest** model driven by the observation that the first state change (parsing the document and computing the match set) must complete *before* the handler can respond to the current request (e.g. `semanticTokens/full` needs the cached tags immediately), but all downstream effects (pushing diagnostics, refreshing semantic tokens for other open files) are independent and can be deferred.

The protocol handler therefore performs the initial synchronous write to the Document Buffer and Binding Match Service, then publishes a `MatchCacheChangedNotification` via MediatR and returns. Downstream internal handlers pick up the notification asynchronously. This prevents the response round-trip from being blocked by the diagnostics pipeline, which may need to re-parse several open feature files.

The trade-off is that the call graph is less linear: a `textDocument/didChange` triggers work across multiple handlers in sequence. The sequence diagrams in [Appendix A](LSP-IDE-Support-Feature-Designs.md) document each chain explicitly. See also [Internal Event Architecture](#internal-event-architecture) in §5.

### Per-IDE capability registration via `--client` flag

OmniSharp's handler base classes (e.g. `SemanticTokenHandlerBase`) register capabilities dynamically by default. Visual Studio requires static registration for semantic tokens and certain other capabilities — it cannot handle `client/registerCapability` reliably for these.

Rather than encoding per-IDE logic inside each handler class, the server accepts a `--client <ide>` flag at startup and uses it to decide, once during `initialize`, whether to register each capability statically or dynamically. This keeps all IDE-specific registration logic in one place (the startup path) while leaving handler implementations client-agnostic.

### Custom `reqnroll/*` notifications for project-system information

LSP has no vocabulary for IDE project systems. The protocol knows about *workspace folders* and *files*, but not about which `.csproj` owns a file, what its output assembly path is, or which package references it declares. All of that is information the binding discovery pipeline needs.

Three custom client→server notifications bridge the gap: `reqnroll/projectLoaded` (build properties), `reqnroll/projectFiles` (file membership), and `reqnroll/projectUnloaded`. Each is optional — a client that cannot produce it omits it and the server degrades gracefully (folder-prefix routing, no membership index). This optionality is deliberate: the three IDE project systems have very different capabilities and timing, so forcing a single synchronous production path would either slow down the fast path (VS) or be impossible (VS Code has no MSBuild project system at all).

Full protocol details are in [§5 Client ↔ Server Custom Notifications](#client--server-custom-notifications).

### Project membership index — replacing folder-prefix inference

A standard LSP server associates a file with a workspace by checking whether its URI falls under a workspace folder. This works for flat, non-overlapping layouts. It fails for MSBuild projects because:

- A project can **link** files that live physically outside its folder (`<Compile Include="..\Other\X.cs" Link="…">`). The linked file's URI falls under a *different* project's folder, so prefix matching assigns it to the wrong project.
- A project can **exclude** files that live inside its folder (`<Compile Remove>`, false `Condition`). Prefix matching re-admits them silently.
- One physical file can belong to **zero, one, or several** projects simultaneously — a genuine many-to-many relation that a single prefix lookup cannot express.

The consequence is not cosmetic. A linked binding `.cs` routed to the wrong project injects bindings into the wrong registry; a linked feature file silently disappears from the **closed-file workspace scan** (the startup pass where the server indexes all `.feature` and `.cs` files that belong to a project but are not currently open in the editor); an excluded-but-opened file can inject phantom bindings into a registry that a subsequent build will wipe. For F15 (Find Unused Step Definitions), a false "unused" result invites deletion of live code.

The solution is an explicit `path → {projects}` index populated by the `reqnroll/projectFiles` notification. Folder-prefix containment is retained only as a read-only fallback for files no project claims. All registry writes, the closed-file scan, and the usages/unused accounting are gated on index membership rather than on the filesystem. The full reproduction, root-cause analysis, and design resolution are in [Feature Designs — Infrastructure](LSP-IDE-Support-Feature-Designs.md#infrastructure-linked-files-and-project-membership); the index design and its invariants are in [§5 Workspace Model](#workspace-model) below.

---

## 3. Module Architecture

```mermaid
graph TB
    subgraph Editors["IDE Clients"]
        VSCode["VS Code Extension\n(TypeScript / vscode-languageclient)"]
        VS["Visual Studio Extension\n(VS.Extensibility + VSSDK fallback)"]
        Rider["Rider Plugin\n(Kotlin — thin wrapper only)"]
    end

    subgraph Server["LSP Server  ·  Reqnroll.IdeSupport.LSP.Server\n(net9+, cross-platform executable)"]
        direction TB
        Handlers["LSP Handlers\n(OmniSharp.Extensions.LanguageServer)"]

        subgraph Core["Reqnroll.IdeSupport.LSP.Core  (netstandard2.0)"]
            GherkinParser["Gherkin Parser\n& AST Builder"]
            DocBuffer["Document Buffer\nService (AST cache)"]
            RoslynDiscovery["Roslyn Discovery\n(source analysis)"]
            BindingRegistry["Binding Registry\n(match cache)"]
            SemTokenSvc["Semantic Token\nService"]
            DiagSvc["Diagnostics\nAggregator"]
            CompletionSvc["Completion\nService"]
            FmtSvc["Formatting\nService"]
            BindingMatch["Binding Match\nService"]
            SymbolSvc["Symbol / Outline\nService"]
            InlayHintSvc["Gherkin Inlay Hint\nService (F23)"]

            RoslynDiscovery --> BindingRegistry
            BindingMatch --> BindingRegistry
            InlayHintSvc --> BindingMatch
        end

        Handlers --> GherkinParser
        Handlers --> DocBuffer
        Handlers --> SemTokenSvc
        Handlers --> DiagSvc
        Handlers --> CompletionSvc
        Handlers --> FmtSvc
        Handlers --> BindingMatch
        Handlers --> SymbolSvc
        Handlers --> InlayHintSvc
    end

    subgraph Connector["Binding Connector  (out-of-process)"]
        ReflectionDiscovery["Reflection Discovery\n(compiled assemblies)"]
    end

    subgraph Common["Reqnroll.IdeSupport.Common  (netstandard2.0)"]
        ConfigLoader["Configuration Loader\n(reqnroll.json / .editorconfig)"]
        Logging["Logging"]
        Telemetry["Telemetry (HTTP)"]
    end

    VS     -->|"JSON-RPC / stdio"| Handlers
    VSCode -->|"JSON-RPC / stdio"| Handlers
    Rider  -->|"JSON-RPC / stdio"| Handlers

    Core   --> ConfigLoader
    Core   --> Logging
    Server -.->|"IPC"| Connector
    Connector -.->|"BindingDiscoveryResult"| BindingRegistry
```

### Transport

All three IDE clients communicate with the server over **stdio**. The server is launched as a child process by the IDE extension and exchanges JSON-RPC messages over its standard input/output streams.

### Parsing, Discovery, and Matching Pipeline

Three distinct components form the core of the server's intelligence. Each has its own caching layer and independent update lifecycle:

**1 · Gherkin Parser & Document Buffer**

On `textDocument/didOpen` and `textDocument/didChange`, the sync handler invokes `DeveroomTagParser`, which runs the Gherkin parser and step-binding match in a **single combined AST walk**. The output is a `DeveroomTag[]` tree that encodes both structural classification (keywords, tags, descriptions, doc strings, data tables, parse errors) and step match results (`DefinedStep`, `UndefinedStep`, `StepParameter`, `ScenarioOutlinePlaceholder`). This tag tree is stored in the Document Buffer keyed by URI. A `FeatureBindingMatchSet` is derived from the match-result tags and stored separately in the Binding Match Service.

All subsequent requests for a document (semantic tokens, outline, folding, diagnostics) read from the cached tag tree; they do not re-parse.

> **Note**: Although `textDocument/didChange` may carry only the incremental text delta, `DeveroomTagParser` always re-parses the entire file. Because Gherkin AST nodes carry absolute location information, inserting or deleting a line shifts the location of every subsequent node; partial re-parse is not practical.

**2 · Binding Registry**

Binding information enters the registry from two sources:

- **Roslyn Discovery** (in-process, in LSP.Core): when a `.cs` file changes, Roslyn re-analyzes the changed file and replaces its bindings in the registry. No build is required; feedback is immediate.
- **Reflection Discovery** (out-of-process Connector): when a build is detected (see [Q9](LSP-IDE-Support-Open-Questions.md) for per-IDE detection reliability), the Connector scans the compiled assembly and replaces the full registry.

**3 · Binding Match Service**

The Binding Match Service holds the `FeatureBindingMatchSet` cache derived from the tag tree (see above). Because matching is fused into the parse pass rather than being a separate stage, the cache is not updated independently of the Document Buffer — both are written together by the sync handler on every `didOpen` / `didChange`.

When the **Binding Registry** changes (C# file save or post-build reflection scan), the server cannot rely on the tag tree already encoding the new match results. `BindingRegistryChangedHandler` therefore re-runs `DeveroomTagParser` for each open feature file against the updated registry, atomically replacing both the tag tree in the Document Buffer and the match set in the Binding Match Service. Any change to the match cache triggers the Diagnostics Aggregator to recompute and push diagnostics for affected files.

---

## 4. Repository Structure

```
Reqnroll.IdeSupport/
├── src/
│   ├── Reqnroll.IdeSupport.Common/             # Shared infrastructure (netstandard2.0)
│   │   ├── Configuration/                      # reqnroll.json, .editorconfig loaders
│   │   ├── Logging/                            # Cross-platform logging abstractions
│   │   ├── ProjectSystem/                      # IDE-agnostic file/project abstractions
│   │   └── Telemetry/                          # HTTP-based telemetry (cross-platform)
│   │
│   ├── Reqnroll.IdeSupport.LSP.Core/           # Protocol-agnostic LSP logic (netstandard2.0)
│   │   ├── Parsing/                            # DeveroomGherkinParser, AST builder
│   │   ├── Discovery/                          # RoslynDiscovery, BindingRegistry
│   │   ├── Matching/                           # BindingMatchService, match cache
│   │   └── Editor/                             # SemanticTokenService, FormattingService, etc.
│   │
│   ├── Reqnroll.IdeSupport.LSP.Server/         # OmniSharp LSP host (net9+, exe)
│   │   ├── Handlers/
│   │   │   ├── Protocol/                       # OmniSharp handler classes (LSP messages)
│   │   │   └── Internal/                       # MediatR notification handlers (internal events)
│   │   ├── Workspace/                          # WorkspaceScopeManager, ProjectScope
│   │   └── Program.cs
│   │
│   ├── Reqnroll.IdeSupport.LSP.Connector.Models/  # DTOs for reflection discovery results
│   ├── Reqnroll.IdeSupport.LSP.Connector/         # Reflection-based binding discovery (exe)
│   │
│   └── clients/
│       ├── visualStudio
│       |   ├── Reqnroll.IdeSupport.VisualStudio.Extension/     # VSIX (net481)
│       │   |   ├── LanguageClient/                 # ReqnrollLanguageClient (VS.Extensibility)
│       │   |   ├── Inspection/                     # LspInterceptingPipe (debug tracing)
│       │   |   └── LSPServer/                      # Embedded server exe
│       |   ├── Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration/  # VSSDK fallback helpers
│       |   └── Reqnroll.IdeSupport.VisualStudio.Wizards*/           # New Project/Item wizards
│       ├── vscode/                             # TypeScript VS Code extension
│       └── rider/                              # Kotlin Rider plugin (thin wrapper)
│
└── tests/
    ├── Reqnroll.IdeSupport.LSP.Core.Tests/         # Unit tests for LSP.Core services
    ├── Reqnroll.IdeSupport.LSP.Server.Tests/        # Unit tests for LSP handlers
    ├── Reqnroll.IdeSupport.LSP.Server.Specs/        # Integration specs (simulates IDE client)
    ├── Reqnroll.IdeSupport.VisualStudio.Tests/      # Unit tests for VS extension
    ├── Reqnroll.IdeSupport.VisualStudio.Specs/      # Integration specs for VS extension
    └── Reqnroll.IdeSupport.Specs/                   # End-to-end BDD specs (Reqnroll)
```

> **Convention**: projects named `*.Tests` are unit tests; projects named `*.Specs` are integration/BDD tests. Client-side unit and integration tests should be considered for each IDE client as the clients mature (see [Q8](LSP-IDE-Support-Open-Questions.md)).

---

## 5. LSP Server Design

The server is a self-contained executable built on `OmniSharp.Extensions.LanguageServer`. It is embedded in each IDE extension package and launched as a child process on extension activation — for Visual Studio, "extension activation" is literal: [§6.2](#62-visual-studio) covers how `LspServerConnectionService` moves process launch off the `.feature`-file-open path onto extension load.

### Capability Registration

OmniSharp supports both static (declared in `initialize` response) and dynamic (via `client/registerCapability`) registration. Visual Studio has known issues with dynamic registration for some capabilities (see per-feature notes).

The server accepts a `--client <ide>` command-line flag at startup (e.g., `--client visualstudio`) so that it can choose static vs. dynamic registration for each capability based on the consuming client, without requiring any client-side override logic.

> **OmniSharp implementation note**: OmniSharp's handler base classes (e.g., `SemanticTokenHandlerBase`) use dynamic registration by default. For capabilities requiring static registration, we will either build alternate base classes or patch the underlying OmniSharp registration — this is a known implementation risk for Phase 1.

### Document Scope

The server registers interest in both `*.feature` files and `*.cs` files. It does not act as a general-purpose C# language server; its interest in `*.cs` files is limited to:

- Receiving `textDocument/didOpen` / `didChange` to trigger Roslyn-based binding re-discovery (see [F2](LSP-IDE-Support-Feature-Designs.md#f2--binding-discovery))
- Providing `textDocument/references` and `reqnroll/findStepUsages` (step usages, from a C# binding method — see [F14](LSP-IDE-Support-Feature-Designs.md#f14--find-step-definition-usages))
- Providing `textDocument/codeLens` (usage counts on binding attributes)

A single OmniSharp text-document sync handler, `TextDocumentSyncHandler`, registers a document selector covering **both** `**/*.feature` and `**/*.cs` and routes by file extension — a single handler avoids OmniSharp's ambiguity when two `TextDocumentSyncHandlerBase` implementations claim overlapping documents. `.cs` files are deliberately **not** stored in the Gherkin document buffer.

### Client ↔ Server Custom Notifications

Beyond the standard LSP surface, each IDE glue layer sends a small set of Reqnroll-specific notifications that carry project-system information LSP itself has no vocabulary for. These are produced by the client/glue (which has access to the IDE's project model) and consumed by the `LspWorkspaceScopeManager`.

| Method | Direction | Purpose |
|---|---|---|
| `reqnroll/projectLoaded` | Client → Server | A Reqnroll project was opened, or its **build properties** changed (rebuild, configuration switch). Carries `workspaceFolder`, `projectFile`, `projectFolder`, `outputAssemblyPath`, `targetFrameworkMoniker`, and resolved `packageReferences`. Cheap to produce; sent early so binding discovery can start as soon as the output path is known. |
| `reqnroll/projectFiles` | Client → Server | The project's **file membership** (feature files + binding source files, on-disk paths, including links). Separate from `projectLoaded` because membership has a different change cadence and, in some IDEs, a different (slower, async) production path. See [Project membership](#project-membership-the-path--projects-index) below and [Q17](LSP-IDE-Support-Open-Questions.md). |
| `reqnroll/projectUnloaded` | Client → Server | A project was removed from the solution/workspace. Carries `projectFile`. |

> **Why `projectFiles` is a separate notification, not fields on `projectLoaded`.** The decision is driven by concrete differences between the three IDE project systems — VS Code cannot produce the manifest as a cheap byproduct of project load at all (it has no MSBuild project system); VS's authoritative item enumeration (CPS / MSBuild evaluation) is a slower, async path than the EnvDTE property reads that power `projectLoaded` today; and Rider can produce it readily but its built-in LSP client's custom-notification transport is not yet proven. An optional, snapshot-plus-delta message decouples fast-path discovery from membership, matches each project system's change events, and degrades gracefully per client. The full rationale is recorded under [Q17](LSP-IDE-Support-Open-Questions.md).

### Workspace Model

Each opened workspace folder maps to an `LspWorkspaceScope` containing one or more `LspProjectScope` instances. Project detection reads `*.csproj` files to discover `reqnroll.json` configuration and output assembly paths for the Binding Connector.

**Multi-root configuration divergence**: In a workspace with multiple root folders (e.g., a monorepo with separate application and test projects), each root may carry a different `reqnroll.json`. The `LspWorkspaceScope` maintains a separate `LspProjectScope` — and thus a separate Binding Registry — per project. Feature files are resolved against the registry of the project(s) that own them, using the authoritative membership index described next. A naive fallback to a merged view of all registries is not realistic for production use.

#### Project membership: the `path → {projects}` index

A file's owning project is **not** inferred from on-disk folder containment. MSBuild allows a project to **link** files that live outside its folder — `<Compile Include="..\Other\X.cs" Link="…">`, `<None Include="…" Link="…">`, the `ReqnrollUseIntermediateOutputPathForCodeBehind = true` pattern — and to **exclude** files that live inside it (`<Compile Remove>`, `<None Remove>`, or a false `Condition`). A single physical file may therefore belong to **zero, one, or several** projects. This is a genuine many-to-many relation that folder-prefix matching cannot express (see [Q17](LSP-IDE-Support-Open-Questions.md) for the analysis and the corpus reproduction that motivated this design).

The server maintains an explicit, authoritative index mapping each file's on-disk path to the **set** of projects that include it, keyed within each project by `(projectFile, targetFrameworkMoniker)` so that per-TFM conditional membership lands on the correct registry. The index is populated by the **`reqnroll/projectFiles`** client→server notification (see below); folder-prefix containment survives only as a clearly-degraded, read-only last resort for files that **no** project claims, and it must never write into a registry or into usages/unused accounting.

Two invariants follow, and both are required for correctness:

1. **Membership is conferred exclusively by the index.** Neither folder containment nor a file being open in the editor may grant a file ownership in any project. The closed-file workspace scan is driven by the index, not by a folder glob — otherwise an *excluded* file physically inside a project folder would be silently re-admitted.
2. **Open-state never confers membership or accounting.** A file the user opens that no project owns receives only registry-independent features (semantic tokens, parse-error diagnostics, folding, formatting, document symbols). Binding-dependent features (unmatched-step diagnostics, step↔binding navigation, binding completion, usages/unused) are suppressed for it — and an opened-but-unowned `.cs` must **not** inject phantom bindings into any registry via the Roslyn live path. Were this not enforced, merely opening an excluded feature file could flip a binding from "unused" to "used" in F15 (Find Unused Step Definitions).

#### The `reqnroll/projectFiles` notification

Each IDE glue layer enumerates a project's feature files and binding source files — resolved to their **on-disk** paths, including linked files — and sends them to the server. This is a **separate** notification from [`reqnroll/projectLoaded`](#client--server-custom-notifications), deliberately decoupled because the two carry information of different cadence and availability (the rationale, including how each IDE's project system constrains the choice, is recorded under [Q17](LSP-IDE-Support-Open-Questions.md)):

| Property | Value |
|---|---|
| Method | `reqnroll/projectFiles` (client → server notification) |
| Key | `projectFile` + `targetFrameworkMoniker` (matches the `reqnroll/projectLoaded` keying) |
| Payload | `{ projectFile, targetFrameworkMoniker, kind: "baseline" \| "delta", files: [{ path, role: "feature" \| "binding", added? }] }` |
| Baseline | A `kind: "baseline"` message carries the project's complete current membership and is the **authoritative snapshot**. Receiving it flips every previously-absent file under that project from *pending* to *excluded*. |
| Delta | A `kind: "delta"` message carries incremental add/remove entries, matching the fine-grained item-change events the VS and Rider project systems surface. |
| Optional | A client that cannot reliably produce the manifest simply omits the notification; that project then falls back to folder-prefix routing. This makes the message safe to adopt per-client (notably for Rider's less-proven built-in LSP transport). |

**Pending vs. excluded.** Because both "deliberately excluded" and "not yet reported" manifest as *absence from the index*, the server treats absence as **pending** (unknown — defer binding-dependent features rather than declaring the file unowned) until the project's first `baseline` arrives, and as **excluded** thereafter. The glue layer must therefore re-send membership not only on project load and rebuild but on **`.csproj` change**, so that re-including a file in the editor restores its ownership.

**Gherkin dialect resolution**: Dialect is resolved at two levels:

1. **Per-project default**: read from `reqnroll.json` (`language` property; default `en`).
2. **Per-file override**: a `# language: <code>` comment on the first line of a `.feature` file overrides the project default for that file. This is standard Gherkin syntax and must take precedence.

The Document Buffer stores the effective dialect alongside each file's AST. The Semantic Token Service and Completion Service always use the per-file effective dialect.

### Debounce, Cancellation, and Request Priority

**Debounce policy**: `textDocument/didChange` events arrive on every keystroke. Rather than immediately triggering the full parse-and-match pipeline on each event, the server applies a configurable debounce window (default: **200 ms**) before publishing `FeatureFileChangedNotification`. This prevents the binding match pipeline from thrashing during rapid typing and avoids unnecessary `publishDiagnostics` pushes mid-word.

**Cancellation**: All protocol handlers that produce responses (semantic tokens, completions, definition) accept a `CancellationToken`. If a superseding request arrives before the previous one completes, the client may send `$/cancelRequest`; OmniSharp propagates this as a cancelled token. Handlers must not leave the Document Buffer or Binding Registry in an inconsistent state if cancelled mid-flight — the previous value must remain valid until the new value is atomically committed.

**Request priority**: Interactive responses take priority over background pushes.

| Priority | Request type | Reason |
|---|---|---|
| Highest | `textDocument/completion` | A delayed completion popup is immediately visible to the user |
| High | `textDocument/definition`, `textDocument/references` | Triggered by deliberate user action |
| Medium | `textDocument/semanticTokens/full` | Coloring lag is noticeable but tolerable for <200 ms |
| Low | `textDocument/publishDiagnostics` | Can be deferred until after interactive responses are served |

### Internal Event Architecture

Protocol handlers (in `Handlers/Protocol/`) are the OmniSharp-based classes that directly handle incoming LSP messages. Rather than orchestrating service calls inline, they publish typed **MediatR notifications** that trigger further processing asynchronously.

Internal handlers (in `Handlers/Internal/`) subscribe to these notifications and perform the actual work, each publishing further notifications in turn. This yields an event-driven pipeline with no single orchestrating manager:

The pipeline uses a **sync-first, async-rest** model. The Protocol Handler directly performs the first state-changing step (parsing and storing in the Document Buffer), because the tag tree is needed synchronously to respond to the current LSP request (e.g., `semanticTokens/full` must return the cached tags immediately). All downstream effects — diagnostics — are then dispatched asynchronously via MediatR.

Parsing and binding matching are **not separate pipeline stages**: `DeveroomTagParser` performs both in a single AST walk (see [F1 · Gherkin Syntax Highlighting](LSP-IDE-Support-Feature-Designs.md#f1--gherkin-syntax-highlighting)), producing a `DeveroomTag[]` that carries both structural classification and step match results together. The sync handler stores that tag tree plus the derived `FeatureBindingMatchSet` in one synchronous step, then publishes a single `MatchCacheChangedNotification` via MediatR:

```
LSP Client message
  → Protocol Handler (OmniSharp base class)
      → [sync] Parses document + matches steps in one AST walk (DeveroomTagParser),
               stores DeveroomTag[] + MatchSet in DocBuffer/BindingMatchService
      → publishes MatchCacheChangedNotification (async, via MediatR)
          → Internal Handler (aggregates diagnostics)
              → pushes textDocument/publishDiagnostics
```

The Protocol Handler is responsible for the initial synchronous state write; MediatR orchestrates the background fan-out — here, diagnostics aggregation and push — only.

**`textDocument/codeAction` scope**: `CodeActionHandler` handles code actions on `.feature` files. Planned actions: "Define missing steps" (F6) and any future quick-fixes on Gherkin diagnostics. Code actions on `.cs` files (e.g., "Generate step definition from binding template") are feasible but deferred; they would be handled by a dedicated `.cs` code action handler. IDEs universally merge code actions from multiple registered language servers for the same file type — the Reqnroll server's actions will appear alongside those from the native C# server in the lightbulb menu without conflict.

**Key protocol handler classes** (one per LSP capability group — several capabilities that a naive one-handler-per-message-type design might split apart are consolidated into a single class, noted below where relevant):

| Class | LSP messages handled |
|-------|---------------------|
| `TextDocumentSyncHandler` | `textDocument/didOpen`, `didChange`, `didClose` — a single handler covers **both** `.feature` and `.cs` documents, routed internally by file extension, rather than one handler per file type (this avoids OmniSharp's ambiguity when two `TextDocumentSyncHandlerBase` implementations claim overlapping documents) |
| `WatchedFilesHandler` | `workspace/didChangeWatchedFiles` |
| `SemanticTokensHandler` | `textDocument/semanticTokens/full`, `/delta` |
| `DefinitionHandler` | `textDocument/definition` (from `.feature` cursors) |
| `CodeActionHandler` | `textDocument/codeAction` |
| `CompletionHandler` | `textDocument/completion`, `completionItem/resolve` |
| `DocumentSymbolHandler` | `textDocument/documentSymbol` |
| `FoldingRangeHandler` | `textDocument/foldingRange` |
| `InlayHintHandler` | `textDocument/inlayHint` (F23 — binding info hints; statically-declared capability, manually registered alongside `FoldingRangeHandler` — see [F23 as-built](LSP-IDE-Support-Feature-Designs.md#f23--inlay-hints-step-binding-info)) |
| `FormattingHandler` | `textDocument/formatting`, `rangeFormatting`, `onTypeFormatting` |
| `ReqnrollCommandHandler` | `workspace/executeCommand` |
| `StepReferencesHandler` | `textDocument/references` (from `.cs` cursors; two-state) |
| `FindStepUsagesHandler` | `reqnroll/findStepUsages` (custom; three-state: isBinding false / 0 usages / locations) |
| `StepRenameHandler` | `textDocument/prepareRename`, `textDocument/rename`, `reqnroll/selectRenameTarget` (retains the session state for a picked disambiguation target between requests) |
| `RenameTargetsHandler` | `reqnroll/renameTargets` (extracted from `StepRenameHandler` in #139 — enumerates candidate binding attributes at the cursor for the multi-attribute picker; stateless) |
| `WorkspaceEditBuilder` | (not a handler) — builds the `StepRenameHandler`/`RenameTargetsHandler` response `WorkspaceEdit`, negotiating per-request between the annotated `DocumentChanges` shape (clients advertising `changeAnnotationSupport`) and the legacy `Changes` map (VS) |
| `RenameChangeAnnotations` | (not a handler) — holds the `reqnroll.rename.feature` / `reqnroll.rename.binding` annotation-id constants consumed by `WorkspaceEditBuilder` |
| `CSharpAttributeLiteralResolver` | (not a handler) — resolves a binding's C# attribute literal (source location + text) shared by `StepRenameHandler` and `RenameTargetsHandler` |
| `RenameBindingResolver` | (not a handler) — read-only binding-resolution primitives (cursor → candidate bindings) shared by `StepRenameHandler` and `RenameTargetsHandler` |
| `NewNameReconciler` | (not a handler) — reconciles the user-entered new name against the existing step-text/regex shape before the edit is built |
| `RenamePostApplyCoordinator` | (not a handler) — post-`WorkspaceEdit` steps: pushes a genuine `workspace/applyEdit` to VS only (its rename pipe swallows the handler's return value) and invalidates the match cache for closed `.feature` files the rename touched |
| `StepCodeLensHandler` | `textDocument/codeLens`, `codeLens/resolve` |

The rename pipeline's `WorkspaceEdit` response is built by `WorkspaceEditBuilder` (`Features/Rename/`, used by both `StepRenameHandler` and `RenameTargetsHandler`), which negotiates per-request whether the client advertised LSP 3.16 change-annotation support (`documentChanges` + `changeAnnotationSupport` in `ClientSettings.Capabilities.Workspace.WorkspaceEdit`) and emits either an annotated `DocumentChanges` edit (grouped/labelled preview — VS Code) or the legacy `Changes` map (VS, which never advertises `changeAnnotationSupport`). `RenameChangeAnnotations` holds the two annotation-id constants (`reqnroll.rename.feature`, `reqnroll.rename.binding`) that label the edit groups. `RenamePostApplyCoordinator` handles what happens after the edit is built: it pushes the edit to VS via a genuine `workspace/applyEdit` (VS's rename pipe swallows the handler's return value, so VS needs a real push rather than relying on its client to apply the response), and invalidates the match cache for closed `.feature` files the edit touched. See [Feature Designs — Rename change annotations](LSP-IDE-Support-Feature-Designs.md#rename-change-annotations---as-built) for the full negotiation and known limitations.

**Key internal MediatR notifications** and the handlers that consume them:

| Notification | Produced by | Consumed by |
|-------------|-------------|-------------|
| `MatchCacheChangedNotification` | `TextDocumentSyncHandler`, after parsing and matching a `.feature` file in one pass (`DeveroomTagParser`, via `GherkinDocumentTaggerService`) — and separately by `BindingRegistryChangedHandler`, after re-matching each open `.feature` file against an updated registry | `DiagnosticsPublishHandler` (pushes `textDocument/publishDiagnostics`); `SemanticTokensPushHandler` (Visual Studio only — pushes `reqnroll/semanticTokens`, see [F1](LSP-IDE-Support-Feature-Designs.md#f1--gherkin-syntax-highlighting)) |
| `BindingRegistryChangedNotification` | `BindingRegistryProviderRouter`, relaying `ConnectorBindingRegistryProvider.BindingRegistryChanged` — raised by both the in-process Roslyn patch (`ICSharpBindingDiscoveryService`, on a `.cs` save) and the out-of-process reflection Connector refresh (on a detected build) | `BindingRegistryChangedHandler`, which re-parses every open `.feature` file against the updated registry and republishes `MatchCacheChangedNotification` for each |

`DeveroomTagParser` fuses Gherkin parsing and step-binding matching into a single AST walk, producing one `DeveroomTag[]` tree that carries both structural classification and match results (see [F1](LSP-IDE-Support-Feature-Designs.md#f1--gherkin-syntax-highlighting)). Because of that fusion, there is no separate "parse" notification stage between a document change and a match-cache update — `TextDocumentSyncHandler` calls the parser synchronously and publishes `MatchCacheChangedNotification` directly once the tag tree and match set are stored.

The C# / Roslyn path follows the same principle of going straight to the relevant service rather than through an extra notification hop: on a `.cs` `didOpen`/`didChange`, `TextDocumentSyncHandler` calls `ICSharpBindingDiscoveryService` directly. That service patches the owning project's `ConnectorBindingRegistryProvider`, which raises its `BindingRegistryChanged` event; `BindingRegistryProviderRouter` publishes the `BindingRegistryChangedNotification` shown in the table above, and the established re-match path runs from there (`BindingRegistryChangedHandler` → re-parse open feature files → `MatchCacheChangedNotification` → semantic-token refresh). The out-of-process reflection discovery (post-build) raises the same `BindingRegistryChangedNotification`, so both discovery sources converge on one re-match path regardless of which one produced the update.

---

## 6. IDE Clients

### 6.1 VS Code

A TypeScript extension under `src/VSCode/` using `vscode-languageclient` v10. Nearly all Gherkin intelligence lives in the LSP server; the extension is intentionally thin. Table cell decoration (T3) is deferred to a future iteration — it requires client-side VS Code decoration APIs that LSP semantic tokens cannot express.

#### Extension manifest (`package.json`)

| Property | Value | Notes |
|----------|-------|-------|
| Publisher / ID | `reqnroll.reqnroll-ide-support` | VS Code Marketplace ID |
| Activation events | `onLanguage:gherkin`, `onLanguage:plaintext` | Server starts when a `.feature` file is opened |
| Language registration | ID: `gherkin`, extensions: `.feature` | Associates `.feature` with the language server |
| Default formatter | Reqnroll extension | `editor.defaultFormatter` for `gherkin` language |
| `editor.formatOnType` | `true` (for `gherkin`) | Enables F12 table auto-formatting as user types |
| `reqnroll.trace.server` | `off` / `messages` / `verbose` | Controls LSP protocol trace level; `verbose` also writes to a log file |
| Main dependency | `vscode-languageclient` v10 | Standard VS Code LSP client library |
| Minimum VS Code | 1.96.0 | First version with full `vscode-languageclient` v10 compatibility |

#### Source components

| File | Purpose |
|------|---------|
| `src/extension.ts` | Entry point: resolves server path, registers command stubs, creates output + trace channels, starts `LanguageClient`, wires `ProjectManager` and `StatusBarManager` |
| `src/projectManager.ts` | Watches `.csproj`/`.sln`/`.slnx` files; sends `reqnroll/projectLoaded` and `reqnroll/projectUnloaded` custom notifications; uses `msbuildEvaluator.ts` for MSBuild property evaluation (v2) |
| `src/msbuildEvaluator.ts` | Shells `dotnet msbuild -getProperty` to populate `OutputAssemblyPath`, `TargetFrameworkMoniker`, `RootNamespace`, and package references from `project.assets.json` |
| `src/statusBar.ts` | Status bar item (right-aligned) that reflects LSP server lifecycle state (`Starting` / `Running` / `Stopped`) via `client.onDidChangeState` |
| `src/lspInspectorLogger.ts` | Creates a `LogOutputChannel` that tees to a timestamped `reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log` file when tracing is enabled; produces the same `{"isLSPMessage":true,...}` JSON format as the VS extension inspector |
| `syntaxes/gherkin.tmLanguage.json` | TextMate grammar — provides keyword/tag/comment colouring before LSP semantic tokens arrive |
| `language-configuration.json` | Comment configuration, bracket pairs, and indentation rules for the `gherkin` language |

#### Startup sequence

1. `activate()` registers command stubs and creates output/trace channels.
2. Server binary is resolved for the host platform/architecture (`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`). If the binary is missing a VS Code error notification is shown.
3. `LanguageClient` is constructed with `--ide vscode` flag and started via stdio.
4. `StatusBarManager` subscribes to `onDidChangeState` immediately so the status bar reflects the `Starting` → `Running` transition.
5. After `client.start()` resolves, `ProjectManager` is instantiated. It scans the workspace for `.csproj` files and sends `reqnroll/projectLoaded` notifications with MSBuild-evaluated properties (v2), falling back to empty fields if `dotnet` is unavailable.

#### Server path resolution (production vs. development)

| Mode | Strategy |
|------|----------|
| Production (packaged `.vsix`) | `{extensionDir}/server/<rid>/Reqnroll.IdeSupport.LSP.Server[.exe]` |
| Development (Extension Dev Host, F5) | Relative path from `src/VSCode` to server `bin/Release/net10.0/win-x64/publish/` |

The server is started with `--ide vscode` and communicates over stdio.

#### Project notification approach (v1/v2)

VS Code has no native MSBuild project system. The extension bridges this with a two-tier strategy:

- **v1 (folder-prefix fallback)**: for `.slnx` / `.sln` files, the extension adds the project to the known set but sends only the folder path — the server uses folder-prefix routing for file membership.
- **v2 (MSBuild evaluation)**: for `.csproj` files, `msbuildEvaluator.ts` shells `dotnet msbuild -getProperty` (with `DesignTimeBuild=true`) to extract `TargetFrameworkMoniker`, `OutputPath`, `AssemblyName`, `RootNamespace`, and `ProjectAssetsFile`. Package references are read from `project.assets.json`. This enables reflection-based binding discovery.

**Known limitation**: linked files (files that appear in multiple projects via MSBuild `Link`) are not supported. This is tracked as risk R4.

#### TextMate grammar (fallback colouring)

`syntaxes/gherkin.tmLanguage.json` covers all Gherkin keywords, tags, comments, doc strings, table delimiters, numeric literals, and placeholders via 10 repository entries. It provides colouring during the interval before the LSP server's first `textDocument/semanticTokens/full` response. Once semantic tokens are active the grammar has no visible effect.

#### LSP inspector logging

When `reqnroll.trace.server` is set to `messages` or `verbose`, the `lspInspectorLogger.ts` module creates a `TeeLogOutputChannel` that:
- Shows trace in the **Reqnroll LSP Trace** Output panel (via the standard `traceOutputChannel` mechanism)
- Writes each entry to a timestamped file:
  - Windows: `%LOCALAPPDATA%\Reqnroll\reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log`
  - macOS: `~/Library/Logs/Reqnroll/reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log`
  - Linux: `~/.local/share/Reqnroll/reqnroll-vscode-inspector-YYYYMMdd-HHmmss.log`

#### Packaging and distribution

- Built with `vsce` (VS Code Extension CLI) and packaged as a `.vsix`
- The LSP server self-contained binaries for all four RIDs are bundled under `server/<rid>/` inside the `.vsix`
- CI publishes all four RIDs in parallel (see `.github/workflows/ci.yml`); the `build-vscode-extension` job downloads all four artifacts and then runs `vsce package`
- Minimum VS Code version: **1.96.0**

### 6.2 Visual Studio

A hybrid extension using **VS.Extensibility** as the primary API, with **VSSDK** as a fallback for capabilities not yet exposed by VS.Extensibility.

| Component | API Used | Reason |
|-----------|----------|--------|
| LSP client (`ReqnrollLanguageClient`) | VS.Extensibility | First-class LSP support |
| Code Lens | VSSDK | Not yet available in VS.Extensibility |
| New Project / Item Wizards | VSSDK | Wizard interfaces not in VS.Extensibility |

The embedded `LSPServer.exe` is published to the VSIX under the `LSPServer/` subfolder and launched on extension activation with `--client visualstudio`.

**Eager server startup (`LspServerConnectionService`)**. VS's own `LanguageServerProvider.CreateServerConnectionAsync` is invoked lazily — VS only calls it once a document matching `ReqnrollLanguageClient`'s `DocumentFilter` (`.feature`) is opened/realized, which would otherwise put process launch, pipe construction, and interceptor wiring on the critical path to the editor becoming usable on the first `.feature`-file-open.

`LspServerConnectionService` (`src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspInterception/LspServerConnectionService.cs`) moves that work earlier without changing VS's own activation contract (VS.Extensibility gives no API to hand VS a pre-built connection ahead of its own call). The mechanism:

- Registered as a DI singleton (`ExtensionEntrypoint.InitializeServices`) and eagerly *resolved* in `ExtensionEntrypoint.OnInitializedAsync` — **not** merely constructor-injected into `ReqnrollLanguageClient`. `ReqnrollLanguageClient` itself is only constructed when VS actually activates the `LanguageServerProvider` (i.e. on `.feature`-file open), so relying on constructor injection alone would only buy a ~20–40ms head start; eager *resolution* in `OnInitializedAsync`, ahead of that construction, is what buys the intended multi-second one.
- `OnInitializedAsync` is `ExtensionCore`'s real "extension load" hook (confirmed by decompiling `Microsoft.VisualStudio.Extensibility.Framework.dll`): `CreateAsync` fires it exactly once, on the **first** service *any* part of the extension provides — not specifically the LSP client. In practice `StepCodeLensProvider` (activates as soon as a `.cs` file is opened) is that first service, and it fires 8–18 seconds before `ReqnrollLanguageClient` would in a "open a `.cs` file first, `.feature` file later" workflow — exactly the scenario this design targets. `ExtensionEntrypoint.OnInitializedAsync` calls `ServiceProvider.GetRequiredService<LspServerConnectionService>()`, which is what actually triggers eager construction; `ReqnrollLanguageClient`'s constructor parameter just retrieves the same already-started singleton later.
- The service's constructor kicks off process launch + pipe/interceptor construction immediately via `ThreadHelper.JoinableTaskFactory.RunAsync`, caching the resulting `JoinableTask<IDuplexPipe?>`.
- `CreateServerConnectionAsync` — whenever VS eventually calls it — just awaits `LspServerConnectionService.GetConnectionAsync()`, which returns the already-in-flight or already-completed task instead of starting the process cold.
- `VsProjectEventMonitor` and the resolved `ITelemetryTransmitter` are still constructed at the pre-existing safe point (`OnServerInitializationResultAsync`, after VS's own `initialize`/`initialized` handshake completes) and stored on settable properties of the service (`ProjectMonitor`, `TelemetryTransmitter`) so interceptors built during eager startup can reference them lazily.
- **Known limitation**: the service hands out the same cached pipe on every `GetConnectionAsync()` call. If VS activates the provider more than once in a session — the still-open multi-tab-restore duplicate-server race (see project memory `vs-package-duplicate-server-q23`) — the second caller gets the already-consumed pipe rather than a fresh process. Resolving that race is tracked separately and was out of scope for making startup eager.

**Proactive binding discovery via the preload side channel**. Launching the server process earlier doesn't by itself make binding discovery happen earlier — the server only runs `reqnroll/projectLoaded`/`reqnroll/projectFiles` discovery once it *receives* those notifications, and OmniSharp's `LanguageServer` (`LspRequestRouter`) defers/queues **all** requests and notifications routed through its own JSON-RPC dispatcher until the client's real `initialize` handshake completes (confirmed by decompiling `OmniSharp.Extensions.LanguageServer.dll`: `_initializeComplete`/`ServerNotInitialized`, and the log string *"Tried to send request or notification before initialization was completed and will be sent later"*). Since VS only sends `initialize` when the `LanguageServerProvider` activates (`.feature`-file open), pushing project data over the normal LSP channel is a no-op until then, regardless of how early the process itself launched.

To route around that gate, `Program.cs` uses `LanguageServer.PreInit(...)` instead of `LanguageServer.From(...)`. Unlike `From`, which blocks inside `Initialize()` awaiting the client's real `initialize` before returning, `PreInit` builds the DI container and constructs the `LanguageServer` object — `.Services` is populated and `ILspWorkspaceScopeManager` resolvable — **without** blocking on the handshake. `Main` starts `ProjectPreloadListener.RunAsync(...)` (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Workspace/ProjectPreloadListener.cs`) against that DI-resolved scope manager, *then* calls `server.Initialize(...)` to perform the real handshake whenever it arrives; the listener is cancelled once `Initialize()` returns, since the side channel has no further purpose after that.

`ProjectPreloadListener` listens on a process-local named pipe (`reqnroll-preload-{pid}`) for `{"method":"reqnroll/projectLoaded"|"reqnroll/projectFiles","params":{...}}` lines and dispatches them **directly** to `ILspWorkspaceScopeManager.HandleProjectLoadedAsync`/`HandleProjectFilesAsync` — bypassing OmniSharp's JSON-RPC dispatcher (and its initialize gate) entirely, since it's a completely separate transport the extension controls end-to-end. `ILspWorkspaceScopeManager.HandleProjectLoadedAsync`'s own "auto-creating workspace scope for project notification" behavior confirms the workspace/project model was already designed to tolerate project notifications arriving before `initialize`'s workspace folders exist.

On the VS side, `LspServerConnectionService.StartAsync` fires `LspProjectPreloadPusher.PushAsync` (fire-and-forget, `src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/LspNotifications/LspProjectPreloadPusher.cs`) right after `Process.Start`. It polls for the DTE solution to finish loading (frequently several seconds *after* `OnInitializedAsync` fires — the solution is often not yet open when the eager service starts), then reuses the same payload-building logic as the regular path (extracted into `VsProjectPayloadBuilder`, shared by both `VsProjectEventMonitor` and the preload pusher) to push a `projectLoaded` + `projectFiles` baseline for every solution project over the named pipe.

When the *real* `reqnroll/projectLoaded`/`projectFiles` notifications eventually arrive over the normal LSP channel (sent by `VsProjectEventMonitor` in `OnServerInitializationResultAsync`, unchanged), they land on an already-loaded project — `HandleProjectLoadedAsync`/`HandleProjectFilesAsync` treat a repeat baseline as an update, not a duplicate (see their doc comments), so this is a safe, idempotent race: whichever arrives first (preload pipe or real LSP channel) does the discovery work; the second is a cheap refresh.

**Project membership (`reqnroll/projectFiles`)**: The current `VsProjectEventMonitor` sources `reqnroll/projectLoaded` from **EnvDTE** (`Project.FullName`, output path, TFM, package references) — cheap synchronous property reads. Producing an *authoritative* membership manifest is a different, heavier path: EnvDTE `ProjectItems` is unreliable for SDK-style projects, glob-defaulted includes, `<Compile Remove>`, conditional items, and linked-file on-disk paths, so the manifest must instead come from **CPS** (`UnconfiguredProject` / `ConfiguredProject` project-subscription dataflow) or an MSBuild evaluation. That source is async and updates on its own schedule, which is why membership rides on the separate `reqnroll/projectFiles` notification rather than blocking `projectLoaded`. The monitor must also subscribe to **item add/remove and `.csproj`-change** events (not only build completion, as today) so it can emit `delta` updates and restore ownership when a file is re-included.

### 6.3 Rider

The Rider plugin is a **Kotlin-only** IntelliJ Platform plugin — there is no ReSharper/.NET-backend half and no MSBuild step anywhere in its own build; see `src/Rider/CONTRIBUTING.md` for the full design rationale. Rider's built-in generic LSP client (`com.intellij.platform.lsp.api`) handles most capabilities directly; the plugin adds Kotlin glue only where that generic client has no rendering-side consumer for a capability (confirmed by decompiling Rider's platform classes — true for codeLens, inlay hints, on-type formatting, and folding) or where a feature needs a bespoke `reqnroll/*` protocol message, or where a built-in editor action's own gating logic excludes `.feature` files entirely (Comment Toggle — see F13 below). See the [Cross-IDE matrix](#64-cross-ide-client-implementation--server-conditional-logic-matrix) in §6.4 for the per-feature breakdown.

**Go to Definition needs no PSI bridge.** Setting `lspGoToDefinitionSupport = true` on the LSP server descriptor is sufficient; Go to Step Definition (F5) and the Define/Scaffold Steps code action (F6) both work through Rider's generic LSP client with zero Rider-specific code, confirmed live. There is no `psi.implicitReferenceProvider`/PSI-bridge class and no `.NET`/ReSharper assembly anywhere in the plugin.

#### Plugin manifest (`plugin.xml`)

| Extension point | Implementation | Purpose |
|---|---|---|
| `platform.lsp.serverSupportProvider` | `ReqnrollLspServerSupportProvider` | Registers the LSP server descriptor with Rider's generic LSP client |
| `fileType` | `ReqnrollFeatureFileType` | Registers `.feature` as the "Reqnroll Feature" language |
| `postStartupActivity` (×3) | `ReqnrollRunnableProjectsListener`, `ReqnrollProjectFilesSync`, `ReqnrollDocumentActivationSync` | Project/file membership and activation sync (F2) |
| `codeInsight.codeVisionProvider` | `StepUsagesCodeVisionProvider` | "N step usages" lens (F18) |
| `editorFactoryListener` (×2) | `ReqnrollFeatureInlayHintsController`, `ReqnrollFeatureFoldingController` | Step-binding-info inlay hints (F23) / Code Folding (F10) |
| `typedHandler` | `ReqnrollFeatureOnTypeFormattingHandler` | `\|`-triggered data-table column realignment (F12) |
| `action` (×3) | `FindUnusedStepDefinitionsAction`, `FindStepUsagesAction`, `GoToHooksAction` | F15 (Tools menu) / F14 (Tools menu + editor context menu) / F17 (Tools menu + editor context menu) |
| `action` | `ReqnrollToggleCommentAction` | F13 (Tools menu + editor context menu + `Ctrl+/` keystroke) |
| `actionPromoter` | `ReqnrollCommentTogglePromoter` | Suppresses the built-in `CommentByLineComment` action from `Ctrl+/` for `.feature` files (F13) |
| `toolWindow` (`id="Reqnroll Structure"`) | `ReqnrollStructureToolWindowFactory` | Feature/Rule/Scenario/Step document outline (F9), as a dedicated tool window (Alt+7) |
| `fileBreadcrumbsCollector` | `ReqnrollFeatureBreadcrumbsCollector` | Editor breadcrumbs above `.feature` files, mirroring the Structure View hierarchy |
| `action` (×2) | `RenameFeatureStepAction`, `RenameCSharpStepAction` | Step Rename refactoring (F16) — editor context menu, `.feature` side bound to Shift+F6; `.cs` side context-menu only |

Rider's declared module dependencies are `com.intellij.modules.rider` and `com.intellij.modules.platform` — **not** `com.intellij.modules.lsp`, which isn't a registered module in Rider's distribution (confirmed via `runIde`: requiring it fails with "no such plugin found"). The `com.intellij.platform.lsp.api.*` classes used throughout are part of the core platform artifact and don't sit behind a separate module dependency.

#### Kotlin source components

| File | Purpose |
|---|---|
| `ReqnrollLspServerDescriptor` | Central configuration point: launch command line, supported files (`.feature`/`.cs`), `lspGoToDefinitionSupport`, `lspSemanticTokensSupport`, `lspFormattingSupport`, the custom `lsp4jServerClass` (adds `reqnroll/*` methods on top of standard `LanguageServer`), and `clientCapabilities` overrides advertising refresh/dynamic-registration support the platform default doesn't (see §6.4) |
| `ReqnrollLspServerSupportProvider` | Registers `ReqnrollLspServerDescriptor` with Rider's generic LSP client |
| `ReqnrollRequestSender` | Sends the custom `reqnroll/findStepUsages`/`reqnroll/findUnusedStepDefinitions`/`reqnroll/goToHooks` requests, the *standard* `textDocument/codeLens`/`inlayHint`/`onTypeFormatting`/`foldingRange` requests that Rider's generic client has no rendering-side consumer for, and the *standard* `workspace/executeCommand` request for `reqnroll.toggleComment` (F13) |
| `ReqnrollNotificationSender` | Sends `reqnroll/projectLoaded`/`projectUnloaded`/`projectFiles`/`documentActivated` |
| `ReqnrollCodeLensRefreshInterceptor` / `ReqnrollInlayHintRefreshInterceptor` | Wrap the platform's `LspServerNotificationsHandler` so `workspace/codeLens/refresh`/`workspace/inlayHint/refresh` also refresh the CodeVision lens / inlay hints — the generic handling has no consumer to notify otherwise |
| `ReqnrollSemanticTokensSupport` | Custom `TextAttributesKey` per `reqnroll.*` legend name, since Rider's default `getTextAttributesKey` only maps the ~23 standard LSP token-type names |
| `ReqnrollServerPathResolver` | Resolves the bundled server executable path relative to the plugin's own installed location |
| `StepUsagesCodeVisionProvider` | Calls the standard `textDocument/codeLens` request directly (via `ReqnrollRequestSender`) and renders through IntelliJ's native `CodeVisionProvider` extension point |
| `ReqnrollFeatureInlayHintsController` | Calls the standard `textDocument/inlayHint` request directly and renders via `Editor.inlayModel` (not a declarative inlay provider — `.feature` has no `ParserDefinition`, which breaks that EP's PSI-language-based dispatch) |
| `ReqnrollFeatureFoldingController` | Calls the standard `textDocument/foldingRange` request directly and renders via `Editor.foldingModel` (not a PSI-based `FoldingBuilder`, same `.feature`-has-no-`ParserDefinition` reasoning as the inlay hints controller); preserves fold regions' expand/collapse state across debounced-edit rebuilds |
| `ReqnrollFeatureOnTypeFormattingHandler` | Calls the standard `textDocument/onTypeFormatting` request directly and applies the returned edits manually |
| `ReqnrollProjectFilesSync` / `ReqnrollRunnableProjectsListener` / `ReqnrollDocumentActivationSync` / `ReqnrollProjectBaseline` / `ReqnrollLspServerReadiness` | Project/file membership and activation sync (F2), sourced from Rider's backend project model |
| `FindStepUsagesAction` / `FindUnusedStepDefinitionsAction` / `GoToHooksAction` / `ReqnrollResultPopup` | F14/F15/F17 actions with a shared result-popup renderer |
| `ReqnrollToggleCommentAction` / `ReqnrollCommentTogglePromoter` | F13 — a plain `AnAction` bound to the platform's own `Ctrl+/` keystroke, paired with an `ActionPromoter` that suppresses the built-in `CommentByLineComment` action from that keystroke for `.feature` files. **Not** an `EditorActionHandler` decoration (the first approach tried, which doesn't work): `CommentByLineCommentAction` is a `MultiCaretCodeInsightAction` that hardcodes its own handler and gates on `LanguageCommenters.forLanguage` finding a `Commenter`, so it never consults `EditorActionManager` at all — confirmed by decompiling both classes |
| `ReqnrollStructureToolWindowFactory` / `ReqnrollFeatureStructureViewBuilder` / `ReqnrollStructurePanel` | F9 — the "Reqnroll Structure" tool window. **Not** a `com.intellij.structureViewBuilder` (`FileType`-keyed `StructureViewBuilderProvider`): that EP resolves through a JDK dynamic proxy that throws `ClassCastException` casting the provider to `StructureViewBuilder` on this Rider platform version (confirmed via `idea.log` and by decompiling the platform's own `KeyedExtensionFactory`). A dedicated tool window sidesteps that EP entirely; `ReqnrollStructurePanel` hosts a `StructureView` built directly from `ReqnrollFeatureStructureViewBuilder` |
| `ReqnrollFeatureBreadcrumbsCollector` | Editor breadcrumbs for `.feature` files. **Not** a `com.intellij.ui.breadcrumbs.BreadcrumbsProvider` (`Language`-keyed — same `ParserDefinition` gap as everywhere else in this plugin, since `.feature` has no PSI language registered); `fileBreadcrumbsCollector` is `VirtualFile`/`Document`/offset-scoped instead, with `requiresProvider()` overridden to `false` |
| `RenameFeatureStepAction` / `RenameCSharpStepAction` / `RenameStepRunner` / `RenameWorkspaceEditApplier` | F16 — Step Rename. `RenameStepRunner` holds the shared "disambiguate via `reqnroll/renameTargets`, prompt, then drive `textDocument/rename`" logic behind both actions, mirroring VS's `RenameStepCommand` / VS Code's `renameDisambiguation.ts`. Rider has no native rename bridge (confirmed by decompiling `LspServerDescriptor` — no `lspRenameSupport`-style customization exists) and the server only proactively pushes `workspace/applyEdit` for Visual Studio, so `RenameWorkspaceEditApplier` applies the returned `WorkspaceEdit` locally, inside one write command |
| `ReqnrollDebugLogger` | File logging, gated by the `reqnroll.devSandbox` system property (set only when launched via `./gradlew runIde`) |

#### Server path resolution

`ReqnrollServerPathResolver` resolves the bundled `server/<rid>/Reqnroll.IdeSupport.LSP.Server[.exe]` relative to the plugin's own installed directory. The server is launched with `--ide rider --log-level <Verbose|Warning>` — `Verbose` only when the JVM was started via `./gradlew runIde` (detected through the `reqnroll.devSandbox` system property Gradle sets only for that task), `Warning` for a real installed plugin.

#### Build system

Pure Gradle, via the `org.jetbrains.intellij.platform` Gradle plugin (Kotlin/JVM, JDK 21 toolchain) — there is no MSBuild step, and Gradle never invokes `dotnet` in CI. The LSP server is a prebuilt artifact, bundled one of two ways:

| Scenario | Mechanism |
|---|---|
| Local dev (`./gradlew runIde`, no `-PlspServerBuildDir`) | The `publishServer` task runs `dotnet publish` for the host OS/arch only |
| CI (`-PlspServerBuildDir=<dir>`) | `publishServer` is skipped; `prepareSandbox` copies whichever `server-<rid>` subdirectories already exist under `<dir>` — pre-built by the shared `test-lsp.yml` job — so Gradle never shells to `dotnet` at all |

Since Rider runs on every desktop OS (unlike VS), the packaged plugin bundles **all four RIDs** (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`); `ReqnrollServerPathResolver` picks the right one at runtime.

Key Gradle tasks: `buildPlugin` (packages the `.zip`), `verifyPlugin` (JetBrains Plugin Verifier — the Marketplace plugin ID deliberately avoids the substring "rider", which the Verifier rejects), `runIde` (local dev sandbox), `test` (JUnit5/`kotlin.test` unit tests).

#### Packaging and distribution

- Published to the JetBrains Marketplace as a `.zip` plugin package
- `pluginSinceBuild`/`pluginUntilBuild` = `243`–`251.*` (`gradle.properties`); developed and verified against Rider `2024.3.5` (`platformVersion`)
- Kotlin 2.0.21, JVM toolchain 21, `org.jetbrains.intellij.platform` Gradle plugin 2.2.1

### 6.4 Cross-IDE client implementation & server-conditional-logic matrix

For every shipped feature, each IDE's **client-side implementation** falls into one of three tiers, and the **server** either behaves identically for every client or branches on `ClientIdeContext.Ide` (the `--ide` flag).

**Client-side tiers**

| Tier | Meaning |
|------|---------|
| **OOB** | Works through the IDE's generic/built-in LSP client for the standard method — no interception, no bespoke rendering code. A declarative config knob (e.g. an opt-in property override, a `package.json` contribution) still counts as OOB; no protocol-level or rendering-level custom code is involved. |
| **Glue** | The request/response is a *standard* LSP method, but the IDE's generic client has no rendering-side consumer for it (confirmed by decompiling Rider's platform for codeLens/inlayHint/onTypeFormatting/foldingRange) or the built-in consumer is unreliable (VS's semantic-token pull), so the client calls the standard method directly and renders the result through the IDE's own native extension points. |
| **Custom** | Requires a bespoke `reqnroll/*`-prefixed protocol message because no standard LSP method expresses the feature (e.g. "Go to Hooks" vs. "Go to Definition") or because disambiguation/workflow needs go beyond what the standard method returns (e.g. Rename's ambiguous-binding picker). |

**Server-conditional** — `Yes` means `ClientIdeContext.IsVisualStudio` (or equivalent) actually changes server behavior for that feature; `No` means the server runs identically regardless of which IDE connected, even where the *client's* protocol choice (OOB vs. Glue vs. Custom) differs per IDE.

| Feature | VS Code | Visual Studio | Rider | Server-conditional? |
|---|---|---|---|---|
| F1 · Syntax Highlighting (semantic tokens) | OOB (`semanticTokenScopes` config) | **Custom** — `reqnroll/semanticTokens` push + `SemanticTokensClassificationInterceptor` + custom classifier, because VS's built-in colorizer only maps the ~23 standard token-type names and its pull is unreliable | Glue — standard pull, but a custom `TextAttributesKey` per legend name + `getTextAttributesKey` override, since Rider's default only maps standard types too | **Yes** — server only pushes `reqnroll/semanticTokens` when `--ide visualstudio` |
| F2 · Binding/Project Discovery (`projectLoaded`/`projectFiles`/`projectUnloaded`) | Custom — `projectManager.ts` + `msbuildEvaluator.ts` shell out to `dotnet msbuild` | Custom — `VsProjectEventMonitor` via EnvDTE/CPS | Custom — `ReqnrollProjectFilesSync`/`ReqnrollRunnableProjectsListener` via Rider's backend project model | No |
| F3/F4 · Diagnostics & Parse Errors | OOB (`textDocument/publishDiagnostics`) | OOB | OOB | No |
| F5 · Go to Step Definition | **Custom** — `reqnroll/goToStepDefinitions` (`stepNavigation.ts`); see open question #126 on why this diverges from the other two | OOB (`textDocument/definition` via `DefinitionHandler`) | OOB — confirmed working via Rider's generic Go To Definition, no custom code | No |
| F6 · Define/Scaffold Steps (code action) | OOB (delegates to `editor.action.quickFix`) | OOB (native quick-fix UI; `ScaffoldTrackingInterceptor` is separate glue for a side effect — registering the newly-created file with the project-membership index — not the code action itself) | OOB — confirmed working via Rider's generic Alt+Enter quick-fix, no custom code | No |
| F7/F8 · Keyword/Step Completion | OOB (`textDocument/completion`) | OOB | OOB | **Yes** — `CompletionHandler` special-cases VS's "empty `CompletionList` on a trigger char reverts the typed character" behavior |
| F9 · Document Outline (hierarchical dropdown bar) | OOB (`textDocument/documentSymbol`, native Outline view) | **Custom** — `reqnroll/documentSymbolHierarchical` + `GherkinNavigationBarSymbolService`/`IVsDropdownBarClient`, since VS's classic dropdown bar needs a shape standard `documentSymbol` doesn't provide | **Glue** — `ReqnrollFeatureStructureViewBuilder`/`ReqnrollStructureToolWindowFactory` render a dedicated Structure View tool window (Alt+7) from the standard `documentSymbol` response, since Rider's declarative `structureViewBuilder` extension point threw `ClassCastException` on this platform version; a separate Navigation Bar/breadcrumbs implementation also ships (#161). Implemented, #163 | No |
| F10 · Code Folding | OOB (`textDocument/foldingRange`) | OOB | Glue — `ReqnrollFeatureFoldingController` calls the standard request directly and renders via `Editor.foldingModel`, since Rider's generic client has no rendering-side consumer for folding either (#162) | No |
| F11 · Document Auto-formatting | OOB | OOB | OOB (config opt-in: `lspFormattingSupport` property override activates the platform's generic `LspFormattingService`) | No |
| F12 · Table Auto-formatting (on-type) | OOB (`editor.formatOnType` + `textDocument/onTypeFormatting`) | OOB | Glue — `ReqnrollFeatureOnTypeFormattingHandler` (a `typedHandler`) calls the standard request and applies edits manually, since Rider's generic client has no bridge for `textDocument/onTypeFormatting` at all | No |
| F13 · Comment/Uncomment | Glue — `workspace/executeCommand` (`reqnroll.toggleComment`) bound to Ctrl+/, applied via the client's native `workspace/applyEdit` handling | Glue — same standard `workspace/executeCommand` route, bound via a VSSDK command-filter redirect | Glue — `ReqnrollToggleCommentAction` (a plain `AnAction`, not an `EditorActionHandler` decoration — that doesn't work for this specific built-in action) bound to the same `Ctrl+/` keystroke via `ReqnrollCommentTogglePromoter` suppressing the built-in action for `.feature`; sends the same `workspace/executeCommand` directly, and the resulting `workspace/applyEdit` is applied natively by Rider's platform `Lsp4jClient`, no consumer glue needed for that half (#159) | No |
| F14 · Find Step Usages | Custom — `reqnroll/findStepUsages` + `QuickPick` | Custom — same message + `NavigationPickerDialog` | Custom — same message + `StepUsagesCodeVisionProvider`/`FindStepUsagesAction` | No |
| F15 · Find Unused Step Definitions | Custom — `reqnroll/findUnusedStepDefinitions` | Custom — same message | Custom — same message + `FindUnusedStepDefinitionsAction` | No |
| F16 · Step Rename Refactoring | Custom — `renameDisambiguation.ts` (`reqnroll/renameTargets` + `reqnroll/selectRenameTarget`) atop standard `textDocument/rename` | Custom — same messages + `RenameStep/*` | **Custom** — `RenameFeatureStepAction`/`RenameCSharpStepAction` + `RenameStepRunner`/`RenameWorkspaceEditApplier`, same `reqnroll/renameTargets` + `reqnroll/selectRenameTarget` messages as VS Code/VS. Implemented, #160 | **Yes** — `RenamePostApplyCoordinator` branches on `IsVisualStudio` for post-apply handling |
| F17 · Go to Hooks | Custom — `reqnroll/goToHooks` + `QuickPick` | Custom — same message + `NavigationPickerDialog` | Custom — same message + `GoToHooksAction`/`GoToHooksRunner`, reusing `ReqnrollResultPopup` (F14/F15's chooser popup) (#158) | No |
| F18 · Code Lens (step usage counts) | OOB — native `CodeLensProvider` via `vscode-languageclient`; click actions reuse F14's custom message | **Custom** — `StepCodeLensService` talks to `LspInterceptingPipe` directly, bypassing VS's built-in LSP code-lens infrastructure entirely; refresh uses the custom `reqnroll/refreshCodeLens` notification | Glue — standard `textDocument/codeLens` called directly via `ReqnrollRequestSender`, rendered through IntelliJ's native `CodeVisionProvider`, since Rider's generic client has no rendering-side consumer for it either | **Yes** — refresh notification differs: custom `reqnroll/refreshCodeLens` for VS vs. standard `workspace/codeLens/refresh` for everyone else |
| F23 · Inlay Hints (step binding info) | OOB (`textDocument/inlayHint`, native rendering) | OOB | Glue — `ReqnrollFeatureInlayHintsController` calls the standard request directly and renders via `Editor.inlayModel`, since Rider's generic client has no rendering-side consumer for inlay hints either | No |

> **This table is authoritative** for current per-IDE implementation status; it supersedes the per-feature "IDE support matrix" tables in the [Feature Designs](LSP-IDE-Support-Feature-Designs.md) doc wherever they disagree. Last verified 2026-07-20.

---

## 7. Binding Connector

The Binding Connector is an out-of-process executable responsible for **reflection-based** binding discovery — scanning compiled Reqnroll assemblies for step definition attributes and hook bindings. The LSP server launches the Connector when it detects that a project's output assembly has changed (via `workspace/didChangeWatchedFiles` on the output path), and communicates with it over IPC.

Roslyn-based (source-level) discovery runs **in-process** within the LSP server as part of `Reqnroll.IdeSupport.LSP.Core`. See the [Parsing, Discovery, and Matching Pipeline](#parsing-discovery-and-matching-pipeline) in §3.

```
In-process (LSP.Core)                Out-of-process
─────────────────────────            ─────────────────────────────
  Roslyn Discovery                     Binding Connector
  (source analysis)                    (Reflection Discovery)
  • No build required                  • Accurate after build
  • Immediate on .cs save              • Handles generated code /
  • Per-file granularity                 runtime-registered bindings
        │                                        │
        └──────────────────┬────────────────────┘
                           ▼
                   Binding Registry
                   (merge strategy)
```

**Merge strategy**: When a `.cs` source file changes, Roslyn-derived bindings for that file replace previous entries for that file. When the Connector returns results after a build, its output replaces the entire registry. This ensures the editor always reflects the latest source edits without waiting for a build, while also capturing anything only visible after compilation.

The Connector code is ported from `Reqnroll.VisualStudio.ReqnrollConnector.Generic` in the legacy VS extension — the team already understands its assembly-loading and discovery logic. One addition beyond the ported code: the Connector also ships .NET Framework TFM variants (net462/net472/net481) that use `AppDomain` isolation to analyze .NET Framework test projects, since the cross-platform LSP server cannot load a .NET Framework assembly into an `AssemblyLoadContext` directly. The LSP server selects the appropriate connector variant based on the target project's TFM.

> **Open question (Q15)**: The IPC mechanism between the LSP server and the Binding Connector has not been finalized. Three candidates are: (a) **stdin/stdout** — server launches Connector as a child process and communicates over its standard streams (simplest, no port conflict); (b) **local named pipe** — supports long-running Connector process that can be reused across builds; (c) **localhost TCP with a randomized port** — most flexible but adds port-management complexity. The choice also affects the security model (see §9 Security) and the Connector process lifecycle answer (Q4). See [Open Questions & Risk Register](LSP-IDE-Support-Open-Questions.md).

---

## 8. Testing Strategy

The test project naming convention is defined in §4. The following describes the testing philosophy and coverage expectations for each tier.

**Unit tests (`*.Tests`)**: Each service in `LSP.Core` is tested in isolation using mock implementations of its dependencies. The Gherkin parser, Semantic Token Service, Formatting Service, and Completion Service are the priority targets. Unit tests must not require a running LSP server or IDE instance.

**Server integration specs (`*.LSP.Server.Specs`)**: A simulated LSP client connects to a real server instance over stdio. Test scenarios are authored as Reqnroll `.feature` files — this is the "eating your own dog food" tier where the project's own specification format drives its own test suite. These specs exercise the full protocol pipeline and are the primary verification gate for each phase.

**VS integration specs (`*.VisualStudio.Specs`)**: Use the VS.Extensibility test host to drive the VS extension in-process. Coverage for VS-specific code paths: static registration, VSSDK Code Lens bridge, and wizard flows.

**End-to-end specs (`*.Specs`)**: Full round-trip tests against real IDE instances (VS, VS Code, Rider) using automation frameworks. Optional in early phases; mandatory before lifting the Preview designation.

**Per-phase coverage gates** (informing the [Phased Roadmap](LSP-IDE-Support-Overview.md#4-phased-roadmap)):

| Phase | Minimum gate |
|---|---|
| 1 | Server unit tests passing; F1 protocol integration spec green on all 3 IDEs in CI |
| 2 | All Phase 2 features covered by `LSP.Server.Specs`; Connector integration test |
| 3 | All Phase 3 features covered; VS integration specs for VSSDK paths |
| 4 | E2E suite passing; all open questions with "Needs testing" status resolved |

**Performance benchmarks**: Latency targets for interactive operations (semantic tokens, completion, definition, diagnostics) and background operations (Roslyn re-discovery, reflection discovery, workspace scan) must be verified against the thresholds defined in [§9 Performance Requirements](#performance-requirements). Benchmarks are established in Phase 1 and re-run as the feature set grows.

**Client-side testing**: VS Code extensions can be tested with `@vscode/test-electron`; Rider plugins with the IntelliJ Platform test framework. Defining and resourcing client-side integration tests is deferred until the respective clients reach Phase 3 maturity.

---

## 9. Cross-Cutting Concerns

### Performance Requirements

The following latency targets apply at **P95** (the 95th-percentile: 95% of requests complete within the stated time; the slowest 5% may exceed it) under typical workspace conditions (≤500 `.feature` files, ≤2,000 step binding patterns):

| Operation | Target |
|---|---|
| `textDocument/semanticTokens/full` | < 100 ms from last `didChange` event |
| `textDocument/completion` — keyword (F7) | < 50 ms |
| `textDocument/completion` — step (F8) | < 150 ms |
| `textDocument/definition` — cache hit (F5) | < 100 ms |
| `textDocument/publishDiagnostics` push | < 500 ms from end of debounce window |
| Roslyn binding re-discovery — single `.cs` file | < 2 s |
| Reflection binding discovery — post-build | < 10 s |
| Initial workspace scan — cold start | < 30 s |

> **Note**: These are design targets, not contractual SLAs. Benchmarks should be established in Phase 1 (against the F1 integration spec) and revisited as the feature set grows.

### Performance Verification

The latency targets above are only meaningful if there is a defined mechanism to confirm them. Two distinct shapes of target need different verification:

- **Interactive round-trips** (`semanticTokens/full`, `completion`, `definition`, `publishDiagnostics`) are phrased *"from last `didChange` event"* — i.e. measured **end-to-end at the protocol boundary**, including JSON-RPC serialization, transport, and the MediatR fan-out. Timing a service method in isolation undercounts them.
- **Batch / throughput operations** (Roslyn re-discovery, reflection discovery, cold-start scan) are coarse enough to confirm with wall-clock timing.

A second axis is *where* assertions run. The targets are **absolute** numbers on representative hardware, but shared CI runners are too noisy to assert absolute thresholds reliably — CI is suited to **relative regression** detection, not absolute pass/fail.

Several verification options were considered, organized as layers:

| Layer | Approach | Confirms | Decision |
|---|---|---|---|
| 1 | **Service-level micro-benchmarks** (BenchmarkDotNet over `LSP.Core` services: parser, matcher, completion) | Algorithmic/compute cost; regression detection | Considered; not adopted initially (compute cost is captured indirectly by Layer 2; revisit if a hot path needs isolation) |
| 2 | **End-to-end protocol benchmarks** — a real server driven by a simulated LSP client over its actual transport, against a pinned representative corpus, reporting per-operation percentiles | The interactive P95 targets *and* the batch targets (cold start, discovery) as phrased | **Will implement** |
| 3 | **CI regression tracking** — run a benchmark suite per-PR on a fixed runner, gate on regression % vs. a stored baseline | Prevents gradual perf creep | Considered; deferred (depends on Layer 2 harness existing first) |
| 4 | **Field instrumentation** — protocol handlers record their own durations and emit them via the existing logging path (and optionally as a telemetry metric), yielding real-world P95 from actual user workspaces | Real-world performance on real hardware/workspaces, where the "typical hardware" assumption is actually exercised | **Will implement** |

**Adopted approach: Layers 2 and 4.** Layer 2 provides reproducible confirmation of the design targets against a controlled workload; Layer 4 validates that those targets hold in the field, where synthetic corpora cannot. Layer 2 absolute thresholds are asserted on a designated reference machine (not shared CI runners). Layers 1 and 3 remain available to adopt later — Layer 3 in particular becomes cheap once the Layer 2 harness exists (the harness already writes JSON in the Layer 3 baseline format, see below).

The Layer 2 harness is `tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks` (console tool) +
`…Benchmarks.Core` (harness/scenario/reporting library), driven against the pinned corpus at
`tests/Performance/Corpus/` (structural-fingerprint-pinned, guarded by `CorpusDriftTests`). It
hosts a real server — in-process over an in-memory pipe by default, or `--out-of-process` over
stdio against the built exe (the production transport) — and reports P50/P95/P99 per operation
against the §9 targets, plus a separate `session` command modelling latency under realistic
concurrent editing load. See [src/LSP/CONTRIBUTING.md](../src/LSP/CONTRIBUTING.md#performance-benchmarking)
for usage. Layer 4 field instrumentation lives under `LSP.Server/Performance/`
(`IOperationDurationRecorder`, sampled `PerfSample` telemetry), wired into nearly every feature
handler (semanticTokens, completion, definition, references, rename, code actions, code lens,
document outline, folding, formatting, inlay hints, find-unused-step-defs, comment toggle, and
text-sync).

The two Roslyn/reflection binding-discovery batch scenarios, and representative bound-state
numbers for definition/step-completion, need a real bindings assembly to measure against —
provided by `tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks.Corpus/`, a small class
library that compiles the committed `Corpus/Bindings/CorpusSteps.cs` into a loadable assembly the
benchmark exe deploys next to its own output. Those two scenarios report as skipped, not faked,
on a run where that assembly isn't present. Layer 1 (micro-benchmarks) and Layer 3 (CI regression
gating) remain deferred by design; the JSON output format already doubles as the Layer 3
baseline. See `docs/Archive/Performance-Verification-Implementation-Plan.md` for full detail.

### Telemetry

The server emits the LSP `telemetry/event` notification (server → client); each IDE
host owns the concrete transmitter — `TelemetryTransmitter` in VS's `VSSDKIntegration` (standard
`Microsoft.ApplicationInsights` SDK, not a hand-rolled HTTP client), `telemetry.ts` /
`TelemetryReporter` in VS Code, and `RiderTelemetryTransmitter` in the Rider plugin (a direct POST
to the Application Insights ingestion REST endpoint via the JDK's `java.net.http.HttpClient`, since
pulling in the Java Application Insights SDK wasn't justified for one event type) — all three
pointed at the same Application Insights resource, each forwarding via a `telemetry/event`
interceptor registered on that IDE's LSP client (`TelemetryEventInterceptor.cs` for VS,
`registerTelemetry` in `telemetry.ts` for VS Code, `ReqnrollTelemetryEventInterceptor.kt` for
Rider). Rider had no such interceptor until issue #255 — every event below was reaching VS and VS
Code but producing zero telemetry for Rider users. This resolved [Q11](LSP-IDE-Support-Open-Questions.md)
in favor of option (c); see the archived `docs/Archive/build-plan-telemetry-capture.md` and
`docs/Archive/plan-refactor-analytics-appinsights.md` for the full design detail.

Separately, the LSP server's own `ITelemetryService` was wired to a permanent no-op
(`NullTelemetryService`), so `MonitorError` calls from LSP.Core were silently dropped in production
regardless of IDE. `LspErrorTelemetryService` (issue #255) forwards `MonitorError` to
`ILspTelemetryService` as an `Error` `telemetry/event`, redacting filesystem paths from the message
first (see Security below).

`ITelemetryService` itself was later split (issue #255/#259) once it became clear only one of its
~16 members — `MonitorError` — was ever genuinely shared between `LSP.Core` and the VS host; every
other member is a VS-lifecycle concern (project wizards, welcome/upgrade dialogs) that `LSP.Core`
has no business depending on, and every LSP-side implementation (`NullTelemetryService`, then
`LspErrorTelemetryService`) had to stub out all of them just to satisfy the interface. `MonitorError`
now lives on its own `IErrorTelemetryService`, which `ITelemetryService` extends; `DeveroomGherkinParser`,
`DeveroomTagParser`, and `CompletionContextResolver` depend on the narrow interface directly, while
`IIdeScope.TelemetryService` (needed by both VS's wizard flows and, via
`ProjectScopeDeveroomConfigurationProvider`/`WatchedFilesHandler`, the LSP server) stays typed as
the full `ITelemetryService` — both interfaces resolve to the same DI singleton on the LSP server. A
related, previously-unnoticed bug surfaced during this split: `LspIdeScope.TelemetryService` was
hardcoded to `NullTelemetryService` rather than the DI-registered service, so errors reported
through that specific access path (e.g. `WatchedFilesHandler`'s config-load exceptions) were
silently dropped even after the `MonitorError` fix above — now fixed by injecting the real service.

The following monitoring events from the existing `Reqnroll.VisualStudio` extension should be carried forward:

| Event | Trigger |
|-------|---------|
| `ExtensionInstalled` | First activation after installation |
| `ExtensionUpgraded` | First activation after version change |
| `ExtensionDaysOfUsage` | Daily active use heartbeat — **implemented**: `WelcomeService` fires it right after incrementing/persisting `status.UsageDays` (issue #255/#259; the underlying day-counting was already live, only the telemetry call was missing) |
| `OpenProject` | Workspace project loaded (includes feature file count) |
| `OpenFeatureFile` | `.feature` file opened — **implemented**: wired into `VsProjectEventMonitor`'s document-activation phase machine (fires exactly once per open-lifetime, on the same `SendNow` transition that triggers `reqnroll/documentActivated` — issue #255/#259; the transmission code already existed but had no caller anywhere) |
| `ReqnrollDiscovery` | Binding discovery completed (success/failure, step count) |
| `CommandGoToStepDefinition` | F5 invoked |
| `CommandGoToHook` | F17 invoked |
| `CommandDefineSteps` | F6 invoked — **implemented** as `"DefineSteps command offered"` (`CodeActionHandler`), sent when the code action is *offered* (undefined-step count, actions-offered count). Not "action taken": the code action's `WorkspaceEdit` is applied entirely client-side (`workspace/applyEdit`), so — unlike F13's `workspace/executeCommand` round trip — the server has no signal for whether the user actually clicked it. Offered count is the closest available proxy |
| `CommandFindStepDefinitionUsages` | F14 invoked — **implemented** as `"FindStepDefinitionUsages command executed"` (`FindStepUsagesHandler`), with `UsagesCount` and a best-effort `IsCancelled` (`cancellationToken.IsCancellationRequested` at completion) |
| `CommandFindUnusedStepDefinitions` | F15 invoked (unused count, files scanned) |
| `CommandRenameStep` | F16 invoked |
| `CommandAutoFormatDocument` | F11 invoked — **implemented** as `"AutoFormatDocument command executed"` (`FormattingHandler`), with an `IsSelectionFormatting` flag distinguishing whole-document from range formatting |
| `CommandAutoFormatTable` | F12 invoked — **deliberately not implemented**: on-type table formatting fires on every keystroke inside a table (`|`/tab/newline), not on a discrete user command, so it's scoped out of usage telemetry the same way the continuous editor features (semantic tokens, completion, etc.) are — perf sampling already covers it (`PerfTargets.OnTypeFormatting`) |
| `CommandCommentUncomment` | F13 invoked |
| `CommandAddFeatureFile` | New `.feature` item added |
| `ProjectTemplateWizardCompleted` | F19 wizard completed (framework selected) |
| `Error` | Unhandled exception (fatal / non-fatal) — **implemented** server-side (`LspErrorTelemetryService`) for LSP.Core exceptions (issue #255); VS-side wizard/dialog exceptions were already transmitted via the pre-existing `TelemetryTransmitter.TransmitExceptionEvent` path |
| `ParserParse` | Feature file parsed (duration, file size, dialect) — **retired, not carried forward** (issue #255/#259): VS no longer parses `.feature` files locally, so this event's whole trigger context is gone; the modern equivalent is the LSP server's perf-sampling telemetry (`PerfSample` events for `textDocument/didOpen`/`didChange`, see Performance Verification above), which times parsing uniformly across every IDE instead |
| `NotificationShown` | User-facing notification displayed (notification ID) |
| `NotificationDismissed` | User-facing notification dismissed |
| `LinkClicked` | External link opened from extension UI |

**Required data model enhancements** over the existing VS extension:

| Field | Rationale |
|-------|-----------|
| `IDEClient` (`visualstudio` / `vscode` / `rider`) | Derived from `--client` flag; enables per-IDE breakdown of all events |
| `DiscoveryType` (`roslyn` / `reflection`) | Added to `ReqnrollDiscovery` event; helps understand cache hit rates and build dependency |
| `ExtensionInstalled` / `ExtensionUpgraded` origin | These events fire before the LSP server starts; must be sent by the IDE client, not the server — reinforces the open question on telemetry origin (Q11) |

### Configuration

- `reqnroll.json` — test framework, binding discovery settings, Gherkin language/dialect
- `.editorconfig` — indentation, line endings (for formatting); Reqnroll reads `indent_size`, `indent_style`, and `end_of_line` for `.feature` files
- IDE workspace settings — server path overrides (for development/debugging); the `REQNROLL_GHERKIN_SERVER_PATH` environment variable (Rider) and equivalent settings in VS and VS Code serve the same purpose

### Security

**Code signing**
- The Visual Studio VSIX must be code-signed before publishing to the Visual Studio Marketplace. Signing is performed in CI using a certificate stored as a GitHub Actions secret.
- The VS Code `.vsix` is published via `vsce` with a verified publisher account; the VS Code Marketplace verifies publisher identity independently.
- The Rider plugin `.zip` is published via the JetBrains Marketplace API using an authenticated token stored as a CI secret.
- The bundled LSP server executable inherits signing from its containing package.

**IPC channel security**: The mechanism connecting the LSP server to the Binding Connector is an open question (see [Q15](LSP-IDE-Support-Open-Questions.md)). Whichever mechanism is chosen, the channel must be restricted to the local machine and accessible only to the process that launched the Connector. Unauthenticated remote access must not be possible.

**Telemetry and privacy**
- The `OpenProject` event must not transmit absolute file paths, project names, or any content that identifies the user's codebase. Only aggregate counts (feature file count, step definition count) are transmitted.
- The `Error` event must scrub exception messages for file paths and user-identifiable strings before transmission — implemented server-side via `LspErrorTelemetryService.RedactPaths` (Windows/UNC/POSIX path patterns replaced with `<path>`) before the message ever leaves the process.
- Telemetry is opt-out, consistent with the existing `Reqnroll.VisualStudio` extension behavior. The opt-out preference is respected uniformly across all three IDE clients.
- A public telemetry data inventory (listing every event and its fields) should be published at project launch.

### CI/CD Pipeline

The project uses **GitHub Actions** as its CI platform, consistent with other Reqnroll repositories.

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | Push / PR to `main` | Build server + all clients, run unit tests, run integration specs |
| `package.yml` | Tag `v*` | Build and package `.vsix` (VS), `.vsix` (VS Code), `.zip` (Rider) |
| `publish-vscode.yml` | Manual dispatch / tag | Publish to VS Code Marketplace via `vsce` |
| `publish-rider.yml` | Manual dispatch / tag | Publish to JetBrains Marketplace via Gradle `publishPlugin` |
| `publish-vs.yml` | Manual dispatch / tag | Publish to Visual Studio Marketplace |

**Build matrix**: The LSP server is built as a self-contained executable for `win-x64`, `linux-x64`, and `osx-arm64` in the `package` workflow. Each IDE extension bundles the platform-appropriate server binary. Testing the server in isolation on Linux in CI is a concrete benefit of the LSP separation from IDE-specific code.

**Phase verification gates**: Each phase milestone is tagged in git. The `package` workflow is not run for intermediate commits; it runs only on milestone tags, producing a set of co-versioned extension packages ready for internal testing before marketplace publication.

### Versioning and Compatibility

**Version numbering**: The LSP server and all three IDE client extensions share a single version number and are released together. This avoids client/server version skew — each IDE extension bundles only the server version it was tested against.

**LSP protocol capability negotiation**: The server declares its capabilities in the `initialize` response. If a client does not advertise support for a capability (e.g., an older IDE version that does not support `textDocument/semanticTokens`), the server must not send that capability's messages for that client session. OmniSharp's capability negotiation handles standard capabilities automatically.

**.NET version**: The server targets `net9` for Phase 1. The project will upgrade to the current .NET LTS version on a cadence aligned with Reqnroll's runtime library. IDE clients are unaffected — they launch the server binary and do not reference it as a library.

**`reqnroll.json` schema**: Breaking schema changes require a migration guide. The Configuration Loader must handle both old and new schemas gracefully during a transition period of at least one major version.

**Preview period**: No breaking-change-free API guarantee is made between Preview releases (pre-Phase 4). The Preview designation signals that the extension is suitable for daily use but that disruptive changes may occur. The designation is lifted when Phase 4 parity is achieved and the E2E test suite passes.

### LSP Message Tracing

An `LspInterceptingPipe` intercepts all JSON-RPC messages at the client side and writes them to a temp file compatible with the [lsp-inspector](https://github.com/lampeplf/lsp-viewer) tool. Available in all IDE clients during development; disabled in release builds. The trace file can be enabled via a workspace setting for advanced diagnostic captures.

### Error Handling and Resilience

**Server crash recovery**: The IDE extension detects LSP server process termination and attempts a restart. The VS.Extensibility `LanguageClient` and VS Code `LanguageClient` have built-in restart policies. A maximum of **3 restart attempts** per IDE session is recommended; after exhaustion, the extension surfaces a `window/showMessage` notification prompting the user to reload the workspace.

**Connector failure modes**: If the Binding Connector process fails (e.g., output assembly locked by the test runner, corrupt binary), the server:
1. Logs the error via `window/logMessage`
2. Retains the most recent valid registry state (Roslyn-derived bindings remain)
3. Surfaces a warning notification via `window/showMessage`
4. Does **not** clear the Binding Registry — the extension operates in degraded-but-functional mode until the next successful Connector run

**AST parse failure**: If the Gherkin parser throws on a document, the handler catches the exception, stores an empty AST with a single parse-error diagnostic covering the file, and returns an empty token list. The Document Buffer is never left in a partially-updated state.

**Cancellation safety**: All internal handlers that write to the Document Buffer or Binding Registry use an atomic store model — the new value is written in full or not at all. Partial writes that could leave inconsistent state are not permitted regardless of cancellation timing.

### End-User Troubleshooting and Logging

**Logging architecture**: The LSP server uses the standard .NET `ILogger` abstraction (registered in `Reqnroll.IdeSupport.Common`). At runtime, log entries flow from `ILogger` → `window/logMessage` notifications → each IDE client's `LanguageClient`, which routes them to the output channel. This design keeps logging infrastructure in one place (the server) and requires no logging code in the IDE client extensions. Whether to also support a file-sink option (writing to a local log file) for users without IDE access to the output channel is an open question — see [Q18](LSP-IDE-Support-Open-Questions.md).

Each IDE client exposes a dedicated output surface for runtime diagnostics:

| IDE | Surface | Channel name |
|---|---|---|
| VS Code | Output panel | `Reqnroll` |
| Visual Studio | Output Window pane | `Reqnroll` |
| Rider | Event Log / Services tool window | `Reqnroll` |

**Default log levels**: Release builds log `Warning` and above. Development builds log `Debug` and above (configurable via workspace settings or the server path override mechanism).

**`window/logMessage`**: The LSP server emits log messages for significant lifecycle events (server started, workspace loaded, discovery completed, errors). These are routed to the IDE output channel by each client's `LanguageClient` implementation.

**`window/showMessage`**: Used for user-facing alerts requiring immediate attention (e.g., server restart exhausted, Connector crash, missing .NET runtime). These appear as notification banners in each IDE.

**Issue reporting**: Users reporting bugs are directed to the GitHub issue tracker. The issue template requests attaching the Reqnroll output channel content. The `LspInterceptingPipe` trace file (see [LSP Message Tracing](#lsp-message-tracing)) can be enabled via a workspace setting for advanced diagnostic captures.

### Server Lifecycle

- The LSP server process is launched by the IDE extension on first `.feature` file open
- It is terminated when the IDE workspace is closed
- A single server instance serves all open workspace folders (multi-root support)
- If the server process terminates unexpectedly, the client restarts it up to 3 times per session before surfacing an error to the user (see [Error Handling and Resilience](#error-handling-and-resilience) above)

---

## 10. Alternatives Considered

This section records key architectural decisions and the alternatives that were evaluated but not chosen. It is intended to prevent revisiting settled decisions and to help new contributors understand *why* the architecture looks the way it does.

### 10.1 · LSP vs. IDE-Native Extension Per IDE

**Chosen**: A single LSP server shared across all three IDE clients.

**Alternative**: Three independent native extensions — one using VS.Extensibility, one using the VS Code extension API directly, and one using Rider's native plugin SDK — with all Gherkin intelligence implemented separately in each.

**Rationale for LSP**:
- Reqnroll intelligence (Gherkin parsing, binding discovery, step matching) is identical across all IDEs. Duplicating it three times would triple the maintenance burden for a small team.
- LSP is the industry standard for this class of tooling; contributors familiar with LSP can contribute to any client without IDE-specific expertise.
- The server can be integration-tested independently of any IDE via protocol simulation (`LSP.Server.Specs`). Native extensions cannot be tested this way.
- IDE-native APIs change with each IDE release; the LSP boundary insulates core logic from those churn cycles.

**Trade-offs accepted**:
- Some capabilities (Code Lens in VS, Go to Definition in Rider, Comment/Uncomment in all three) still require IDE-specific plugin code. The LSP boundary reduces but does not eliminate client work.
- The `--client` flag and static/dynamic registration complexity would not exist in a fully native approach.

---

### 10.2 · OmniSharp.Extensions.LanguageServer vs. Alternatives

**Chosen**: `OmniSharp.Extensions.LanguageServer` v0.19.9.

| Alternative | Why not chosen |
|---|---|
| `Microsoft.VisualStudio.LanguageServer.Protocol` | Low-level; no handler framework; requires writing all protocol dispatch manually |
| `StreamJsonRpc` alone | JSON-RPC only; no LSP semantics, capability negotiation, or handler base classes |
| Build from scratch | Not justified given OmniSharp's maturity and the community's existing familiarity with it |

**Risk acknowledged**: OmniSharp.Extensions.LanguageServer is a community library, not a Microsoft-owned product. If it becomes unmaintained, the migration path is to `Microsoft.VisualStudio.LanguageServer.Protocol` with a thin handler layer. This risk is accepted because `LSP.Core` business logic is insulated from the framework layer by the MediatR notification boundary — switching the framework would not require rewriting Gherkin parsing, binding discovery, or matching logic.

**Protocol version ceiling**: `OmniSharp.Extensions.LanguageServer` 0.19.9 implements up to **LSP 3.17** — verified by inspecting the shipped DLL's protocol types and converters. Any feature introduced in **LSP 3.18 or later** is not modelled by the library at all: the server would need hand-rolled request/response DTOs and JSON converters (no `OnRequest<T,>` base-class support to build on), rather than the "already modelled, no custom DTO plumbing" pattern every implemented feature in this document has relied on so far. This is a hard capability boundary, not a configuration choice — confirmed concretely by [Q19](LSP-IDE-Support-Open-Questions.md), where the library's pull-diagnostics (LSP 3.17+) *write*-side JSON converters turned out to be `NotImplementedException` stubs even though the request shape itself is nominally 3.17. Any future feature proposal that depends on a 3.18+ protocol addition must budget for that custom DTO work up front, or evaluate the [alternatives above](#102--omnisharpextensionslanguageserver-vs-alternatives) instead.

---

### 10.3 · MediatR vs. Direct Service Calls for Internal Events

**Chosen**: MediatR notifications for internal server event dispatch (see [Internal Event Architecture](#internal-event-architecture)).

**Alternative**: Direct method calls from Protocol Handlers to services (e.g., `FeatureSyncHandler` calls `GherkinParser.Parse()` directly, then `BindingMatchService.Reconcile()`, etc.).

**Rationale for MediatR**:
- New internal handlers can be added (e.g., a future `HookMatchInternalHandler`) without modifying existing protocol handlers.
- The diagnostics aggregation pattern — where multiple independent services contribute to a single `publishDiagnostics` push — requires a fan-in event model. Direct calls would create tight, hard-to-test coupling between the parse pipeline and the diagnostics pipeline.
- Unit tests can inject test notification handlers to verify pipeline behavior without standing up a full LSP server instance.

**Trade-off accepted**: MediatR adds indirection that makes the call graph harder to follow in a debugger. The sequence diagrams in the Feature Designs document the intended flow explicitly to compensate.

---

### 10.4 · Roslyn In-Process vs. Out-of-Process

**Chosen**: Roslyn Discovery runs **in-process** within `LSP.Core`; only Reflection Discovery runs out-of-process in the Binding Connector.

**Alternative**: Both Roslyn and Reflection Discovery run in the out-of-process Connector.

**Rationale for in-process Roslyn**:
- The primary value of Roslyn Discovery is **immediacy** — updating the binding registry as the user edits a `.cs` file, without waiting for a build. An out-of-process Roslyn would require serializing the full binding payload over IPC on every keystroke event, introducing latency that defeats the purpose.
- Running Roslyn in-process eliminates the IPC round-trip for the common case. Only the expensive post-build reflection scan goes out-of-process.

**Trade-off accepted**: `LSP.Core` carries a Roslyn dependency, increasing the server's startup footprint and assembly size. Roslyn is a stable, mature dependency in the .NET ecosystem; this is accepted.

---

## 11. Non-Feature Engineering Tasks

This section tracks engineering work that is **not** an end-user feature (F1–F20) but is required to support development, verification, or operation of the project. Unlike the Open Questions (which are decisions to be made), these are agreed work items awaiting scheduling.

| # | Task | Related to | Status |
|---|------|-----------|--------|
| T1 | **Performance benchmarking harness** — a console/test harness that launches a real LSP server, drives it through a simulated client over its actual transport, and reports per-operation latency percentiles against the §9 targets (Performance Verification, Layer 2). Asserts absolute thresholds on a designated reference machine. | [Performance Verification](#performance-verification) | **Done** — `tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks(.Core)`; see [src/LSP/CONTRIBUTING.md](../src/LSP/CONTRIBUTING.md#performance-benchmarking). Binding-discovery batch scenarios need a built corpus assembly (see §9 note) — tracked separately, not blocking. |
| T2 | **Representative benchmark corpus** — a pinned, versioned set of `.feature` files and binding patterns matching the "typical workspace conditions" (≤500 feature files, ≤2,000 binding patterns), used as the controlled workload for T1. Includes a generator or curation script so the corpus is reproducible. | [Performance Verification](#performance-verification) | **Done** — `tests/Performance/Corpus/`, structural-fingerprint-pinned (`corpus.manifest.json`), guarded by `CorpusDriftTests`; regenerable via `Benchmarks generate-corpus`. |
| T3 | **Field performance instrumentation** — wrap protocol handlers to record their own durations and emit them via the existing logging path (and optionally as a telemetry metric), for real-world P95 measurement (Performance Verification, Layer 4). | [Performance Verification](#performance-verification), [Telemetry](#telemetry) | **Done** — `LSP.Server/Performance/` (`IOperationDurationRecorder`, sampled `PerfSample`), wired into nearly every feature handler (semanticTokens, completion, definition, references, rename, code actions, code lens, document outline, folding, formatting, inlay hints, find-unused-step-defs, comment toggle, text-sync). |
| T4 | Retrofit Reqnroll.VisualStudio.Specs tests to new code | [Testing Strategy](#8-testing-strategy) | Open |
