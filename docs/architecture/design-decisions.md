# Architectural Decisions and Alternatives Considered

**Status:** Register. Records the main architectural choices and the alternatives that were rejected, so they are not revisited by accident. Product-level decisions and open questions are in the [Open Questions register](../LSP-IDE-Support-Open-Questions.md).
**Audience:** Maintainers and agents about to propose a structural change.

This is a register rather than a design document, so it does not follow the diagram-and-glossary template.

## 1. One LSP server, three thin clients

**Chosen:** a single LSP server shared by Visual Studio, VS Code and Rider.
**Rejected:** three native extensions, each implementing Gherkin parsing, binding discovery and matching separately.

Why:

- The Reqnroll intelligence is identical across IDEs. Writing it three times triples maintenance for a small team.
- LSP is the industry standard for this kind of tooling, so contributors can work on any client without IDE-specific
  expertise.
- The server can be integration-tested alone, by protocol simulation. Native extensions cannot.
- IDE APIs change with each release; the LSP boundary insulates the core logic from that churn.

Accepted cost: some capabilities still need IDE-specific code (CodeLens in Visual Studio, several features in Rider,
comment toggling in all three), and the `--ide` flag with static/dynamic registration would not exist in a fully native
design. See [ide-clients.md](ide-clients.md).

## 2. `OmniSharp.Extensions.LanguageServer` as the framework

**Chosen:** `OmniSharp.Extensions.LanguageServer` 0.19.9.

| Alternative | Why not |
|---|---|
| `Microsoft.VisualStudio.LanguageServer.Protocol` | Low level; no handler framework, all dispatch written by hand. |
| `StreamJsonRpc` alone | JSON-RPC only; no LSP semantics, capability negotiation or handler base classes. |
| Build from scratch | Not justified given OmniSharp's maturity and familiarity. |

Accepted risk: it is a community library, not a Microsoft product. If it becomes unmaintained, the path is to
`Microsoft.VisualStudio.LanguageServer.Protocol` with a thin handler layer. `LSP.Core` is insulated from the framework
layer, so parsing, discovery and matching would not be rewritten. Accepted constraint: it implements LSP up to 3.17;
see [server-protocol.md](server-protocol.md).

## 3. MediatR for downstream events

**Chosen:** MediatR notifications for internal fan-out after the first synchronous state write.
**Rejected:** protocol handlers calling services directly in sequence.

Why: new internal handlers can be added without touching protocol handlers; diagnostics aggregation needs fan-in from
several independent services, which direct calls would couple tightly; tests can inject notification handlers instead of
standing up a server. Accepted cost: indirection that is harder to follow in a debugger, compensated by the flow diagrams
in [server-pipeline.md](server-pipeline.md).

## 4. Roslyn in process, reflection out of process

**Chosen:** Roslyn source discovery runs in the server; only the post-build reflection scan runs in the Connector.
**Rejected:** both out of process.

Why: Roslyn's value is immediacy as the user edits, and out of process it would serialize the full binding payload over
IPC on every edit. Accepted cost: `LSP.Core` carries a Roslyn dependency, which increases startup footprint; Roslyn is
stable and mature. See [binding-discovery.md](binding-discovery.md).

## 5. Client relays telemetry

**Chosen:** the server emits `telemetry/event`, each host transmits.
**Rejected:** direct HTTP from the server (misses pre-server events), or direct HTTP from each client (telemetry code in
three clients). See [telemetry.md](telemetry.md).

## 6. Where code goes: Core versus Server

The rule is about plumbing, not size. `LSP.Core` holds whatever implements Reqnroll functionality without needing
hosting, OmniSharp, MediatR, DI registration or process lifetime. `LSP.Server` holds the plumbing and what is inseparable
from it, including the timing and scheduling semantics of LSP operations. Dependency is one way: Server references Core,
never the reverse, so a type Core needs moves to Core even if it looks like a protocol DTO. See the hub,
[../LSP-IDE-Support-Architecture.md](../LSP-IDE-Support-Architecture.md).

## 7. Telemetry consent: opt-out by default, opt-in only where a marketplace requires it

**Chosen:** the Beta ships the current behaviour (opt-out, controlled by `REQNROLL_TELEMETRY_ENABLED` only). The final
release closes the compliance gaps tracked in #1239, with a per-client gate and opt-in only for Rider.
**Rejected:** opt-in everywhere; reading Visual Studio's own telemetry opt-in.

Why:

- The Beta is a preview meant to gather feedback and matches the previously published plugins.
- Rider requires opt-in (JetBrains Marketplace Approval Guidelines §2.2(d)), so Rider gets a persisted consent state
  (#1244). No other marketplace was found to require it.
- Opt-in elsewhere would yield too little data: the user base is small and a low opt-in rate could not support decisions.

Effective gate per client in the final release (every term must allow transmission):

| Client | Gate |
|---|---|
| Rider | persisted consent AND `REQNROLL_TELEMETRY_ENABLED` (#1244) |
| VS Code | `vscode.env.isTelemetryEnabled` AND `reqnroll.telemetry.enabled` AND `REQNROLL_TELEMETRY_ENABLED` (#1245) |
| Visual Studio | plugin Options-page setting AND `REQNROLL_TELEMETRY_ENABLED` (#1249) |

- `REQNROLL_TELEMETRY_ENABLED` is a veto only: it can turn telemetry off, never grant consent. `=0` stays an
  unconditional kill switch (CI depends on it, #1194).
- The user preference lives in an IDE setting, not an environment variable. The other `REQNROLL_*` telemetry variables
  (`REQNROLL_TELEMETRY_ENV`, `REQNROLL_DEBUG_TELEMETRY_CONNECTION_STRING`, `REQNROLL_TELEMETRY_DEBUG_LOG`,
  `REQNROLL_PERF_TELEMETRY_SAMPLE`, `REQNROLL_FEATURE_USAGE_FLUSH_INTERVAL_SECONDS`) are kept as developer/test-only
  controls.
- The LSP server always emits `telemetry/event` and does not read the flag; only the clients gate (drop events when
  disabled), so revoking consent takes effect immediately. Each client's gate needs a test at its receive point.
- **Visual Studio non-integration.** We deliberately do not read `TelemetrySession.IsOptedIn` / the VSCEIP setting. No
  marketplace rule requiring it was found, and it appears to govern data sent to Microsoft, not data sent to our own
  channel. This is a reading of public documents, not a Microsoft statement: the finding is "no requirement found".
- The optional local debug log (`REQNROLL_TELEMETRY_DEBUG_LOG`) is local-only and independent of consent.

Accepted cost: the clients differ in consent model, so the privacy docs (#1248) must state each client's behaviour.
Related: #1239, #758, #851, #862, #583, #1194. See [telemetry.md](telemetry.md).
