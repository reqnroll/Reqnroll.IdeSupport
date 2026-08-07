# Contributing to the Reqnroll LSP Server

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (matches the `net10.0`
  `TargetFramework` used across the solution's `.csproj` files)
- A `.feature`-file-aware editor is optional for server-only work — the integration specs exercise
  the server directly without any IDE attached (see below)

## Repository layout

```
src/LSP/
  Reqnroll.IdeSupport.LSP.Core     ← Gherkin parser, binding registry, match cache (netstandard2.0, IDE-agnostic)
  Reqnroll.IdeSupport.LSP.Server   ← the LSP server (net10.0 console exe, OmniSharp.Extensions.LanguageServer host)
    Hosting/                       ← Program.cs, DI wiring (ConfigureServer), LanguageServerOptions extensions
    Handlers/ProtocolHandlers      ← standard/custom LSP message handlers (registered via OnRequest/OnNotification
                                      or OmniSharp base classes — see "Handler naming and registration" below)
    Handlers/InternalHandlers      ← MediatR notification handlers for internal pipeline events
                                      (e.g. BindingRegistryChangedNotification, MatchCacheChangedNotification)
    Workspace/                     ← ILspWorkspaceScopeManager — the two-tier folder/project model
    Discovery/                     ← BindingRegistryProviderRouter, ConnectorBindingRegistryProvider
                                      (out-of-proc reflection discovery) and CSharpBindingDiscoveryService
                                      (in-proc Roslyn source-level discovery)
  Reqnroll.IdeSupport.LSP.Connector ← out-of-process reflection-based binding discovery, one variant per TFM
                                      (Reqnroll-Generic-net8.0, -net481, …); invoked as a short-lived child
                                      process by the server, never referenced in-proc

src/Core/Reqnroll.IdeSupport.Common ← shared config/logging/analytics contracts, referenced by everything
```

Start with [docs/LSP-IDE-Support-Architecture.md](../../docs/LSP-IDE-Support-Architecture.md) — it's
the canonical map of the server's internals (workspace model, membership index, discovery pipeline,
event flow). [docs/LSP-IDE-Support-Feature-Designs.md](../../docs/LSP-IDE-Support-Feature-Designs.md)
has the per-feature (F1–F20) design and as-built notes for whatever handler you're touching.

## Building

```sh
dotnet build src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj
```

The server is a self-contained, cross-platform executable. Each IDE client bundles its own
publish of it (see the client-specific CONTRIBUTING guides), so you don't normally need to
`dotnet publish` for local server-only work — `dotnet build`/`dotnet test` is enough.

## Testing

Three layers, in increasing order of end-to-end fidelity:

```sh
# Unit tests (xUnit + NSubstitute + AwesomeAssertions — note: Should() is AwesomeAssertions, not FluentAssertions)
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Reqnroll.IdeSupport.LSP.Core.Tests.csproj

# Integration specs — Reqnroll .feature BDD scenarios, run against a REAL server hosted in-process
# over an in-memory pipe (LspServerHarness / LspScenarioContext), simulating an IDE client.
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Reqnroll.IdeSupport.LSP.Server.Specs.csproj
```

Some spec scenarios are `@ignore`-tagged for genuinely unimplemented features — seeing
`Skipped: N` in the spec run output is expected, not a failure.

For end-to-end IDE verification (does a change actually behave correctly when a real IDE talks to
the server), you need one of the IDE clients — see
[../VisualStudio/CONTRIBUTING.md](../VisualStudio/CONTRIBUTING.md) or
[../VSCode/CONTRIBUTING.md](../VSCode/CONTRIBUTING.md).

## Performance benchmarking

The server ships with a dedicated benchmarking tool implementing
[Performance Verification Layer 2](../../docs/LSP-IDE-Support-Architecture.md#performance-verification)
(reproducible end-to-end protocol benchmarks against a pinned corpus) — separate from
`dotnet test` because it's a measurement *tool* run on demand, not an assertion suite you want in
the normal test sweep.

```
tests/Performance/
  Reqnroll.IdeSupport.LSP.Server.Benchmarks       ← console tool (CLI, entry point)
  Reqnroll.IdeSupport.LSP.Server.Benchmarks.Core  ← harness, scenarios, latency recorder, reporters
  Corpus/                                          ← the pinned, committed benchmark workload
```

The corpus (`tests/Performance/Corpus/`) is **pinned by what's committed**, not by a byte hash —
`corpus.manifest.json` records a structural fingerprint (feature/scenario/step counts, binding
pattern count, and the bound/unbound/ambiguous match-mix), and `CorpusDriftTests` (in the regular
`Reqnroll.IdeSupport.LSP.Server.Tests` suite) asserts the committed corpus still matches it. If
that test fails, something changed the corpus's *shape* — find out why before assuming it's safe
to re-pin.

### Running it

```sh
# Isolated per-operation numbers — the "contract check" against the §9 targets.
dotnet run --project tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks -- run

# Full CLI reference (commands, options, corpus regeneration) — always up to date, read this first:
dotnet run --project tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks -- --help
```

By default the server is hosted **in-process** over an in-memory pipe (fast, reproducible, no
process boundary). Pass `--out-of-process` to instead spawn the built server exe and talk to it
over stdio — the real production transport, including actual process-launch cost; use this when
you specifically want to gauge process/stdio overhead or measure realistic cold-start latency.

Useful options on `run`:

| Option | Purpose |
|---|---|
| `--iterations <n>` / `--warmup <n>` | Measured / discarded iterations per interactive operation (defaults: 50 / 10) |
| `--files <n>` | How many corpus feature files to cycle through (default 10) — avoids measuring one over-warmed document |
| `--out <path>` | Write results as JSON (same shape as the future CI regression-tracking baseline) |
| `--corpus-assembly <path>` | Path to a **built** corpus bindings assembly; enables the two binding-discovery batch scenarios (skipped, never faked, without it) |
| `--assert` | Enforce the absolute §9 targets and exit non-zero on a miss — **only on a designated reference machine**, shared/CI hardware is too noisy for absolute pass/fail (or set `REQNROLL_PERF_REFERENCE_MACHINE=1`) |
| `--no-batch` | Skip the batch scenarios (cold-start scan) for a quick run |

There's also a `session` command that models one user actively editing a document — bursts of
requests (semantic tokens, outline, folding, completion) racing the diagnostics push, with a
configurable fraction cancelled mid-flight to exercise `$/cancelRequest`. It measures latency
*under load*, so its numbers will be ≥ the isolated `run` numbers by design; it's report-only
(no `--assert`), meant to catch load-dependent regressions the isolated numbers can't see.

**Regenerating the corpus** (only when you deliberately want to change its size/shape — e.g. you
changed the generator's feature/pattern counts):

```sh
dotnet run --project tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks -- generate-corpus
```

This rewrites `Features/`, `Bindings/`, `reqnroll.json`, and `corpus.manifest.json` under
`tests/Performance/Corpus/` — review the diff and commit it; committing the regenerated files *is*
what re-pins the corpus. Don't regenerate just to make a failing `CorpusDriftTests` pass without
understanding why it failed first.

## Handler naming and registration

- **Name handlers after the LSP message they implement, not their role in the internal pipeline**
  (e.g. `TextDocumentSyncHandler`, not `DocumentChangeCoordinator`) — keeps handler names
  discoverable against the LSP spec.
- **Prefer OmniSharp's own base interfaces/classes + `options.AddHandler<T>()`** for standard LSP
  methods; that gets you dynamic capability registration for free. Fall back to manual
  `options.OnRequest`/`OnNotification` registration (see `LanguageServerOptionsExtensions.cs`) only
  for custom `reqnroll/*` methods, or where a specific client (usually VS) has a proven issue with
  dynamic registration for that capability — don't reach for the manual path by default.
- Custom `reqnroll/*` protocol surface (params DTOs, method-name constants) lives under
  `Protocol/` — add new custom methods there rather than inline string literals.

## Server logging and trace verbosity

The server accepts three independent command-line flags, each defaulting to a quiet level so a
normal session doesn't write maximum-verbosity output indefinitely. All are parsed in
`Hosting/Program.cs` (`ParseLogLevel`/`ParseProtocolLogLevel`/`ParseTraceLevel`):

| Flag | Values | Default | Controls |
|---|---|---|---|
| `--log-level` | `Off`/`Error`/`Warning`/`Info`/`Verbose` | `Warning` | The server's own app-level `IDeveroomLogger` file (`reqnroll-*-server-*.log`) — parses, discovery, handler activity. |
| `--protocol-log-level` | `Off`/`Error`/`Warning`/`Info`/`Verbose` | `Warning` | OmniSharp's own internal diagnostics (request dispatch, DryIoc, JSON-RPC plumbing), fed to both `window/logMessage` and a dedicated `reqnroll-*-protocol-*.log` file. Deliberately independent of `--log-level` — turning up app logging shouldn't also flood the client's Output panel with library internals, and vice versa. |
| `--trace` | `Off`/`Messages`/`Verbose` | `Off` | F41: seeds the LSP protocol trace level (`$/logTrace`) before the client connects. |

`--trace`'s value is only a starting point — the effective trace level is resolved with the
following precedence (see `ConfigureServer`'s `initialTrace` parameter and
`ResolveInitialTrace`):

1. The `--trace` command-line default.
2. `InitializeParams.Trace`, applied once in `OnInitialized` — but only when the client actually
   sent something other than `Off`, since `Off` there is indistinguishable from "the client didn't
   set this field at all" and must not silently clobber an explicit `--trace` default.
3. `$/setTrace` (`SetTraceNotificationHandler`), which can set any value — including back to
   `Off` — at any time after that.

Each IDE's glue component sets its own defaults for these three flags when spawning the server;
see [../VisualStudio/CONTRIBUTING.md](../VisualStudio/CONTRIBUTING.md) and
[../VSCode/CONTRIBUTING.md](../VSCode/CONTRIBUTING.md) for what each one passes.

## Debugging

Runtime logs land in `%LocalAppData%\Reqnroll\` (Windows) / `~/.local/share/Reqnroll/`
(macOS/Linux):

- `reqnroll-<ide>-debug-<date>.log` — the server's own log output (parses, discovery, handler
  activity, stack traces on server-side failures). Appended across runs/processes sharing a day.
- `reqnroll-<ide>-inspector-<datetime>.log` (or the client's own equivalent) — client-side JSON-RPC
  trace of everything that actually crossed the wire; the source of truth for "what did the client
  send/receive."

When a bug report only makes sense with both, ask for both logs together rather than guessing from
one side.

## Multi-IDE considerations

The server serves three IDE clients (VS, VS Code, Rider — all three built, none yet published to their respective marketplaces) from one codebase.
Before adding IDE-specific behavior in server code:

- Check `ClientIdeContext.IsVisualStudio` (or the equivalent per-IDE flag) and gate the workaround
  explicitly — don't let one IDE's quirk leak into the generic path.
- If the workaround changes observable protocol behavior, add or adjust the relevant spec scenario
  under a client-specific `.feature` file rather than folding IDE-conditional assertions into a
  generic one.
- Check [docs/LSP-IDE-Support-Open-Questions.md](../../docs/LSP-IDE-Support-Open-Questions.md) —
  several open questions are specifically "does IDE X do Y reliably," and an untested assumption
  there is a common source of subtle bugs.
