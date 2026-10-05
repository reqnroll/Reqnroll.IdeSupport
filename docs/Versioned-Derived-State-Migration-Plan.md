# Versioned Derived State — Migration Plan

**Status: PROPOSED (2026-10-05).** Tracking issue: #906. Design:
[Versioned-Derived-State-Design.md](Versioned-Derived-State-Design.md). Section references (§) are to
that document; R1–R4 are the stale-write paths in its §1.3.

This plan says what to build, in what order, and what "done" means for each step. Each phase is
**one PR**: it can ship on its own, it leaves `main` releasable, and it can be undone with a plain
revert. Nothing changes on the LSP wire or in any client, so the VS, VS Code and Rider extensions are
untouched throughout.

---

## Phase overview

| Phase | What | Changes behaviour? | Fixes | Size |
|---|---|---|---|---|
| 0 | Characterisation tests for R1–R4 (skipped until fixed) | No | — | S |
| 1 | `IDocumentGenerations` + bump wiring | No (nothing reads it yet) | — | S |
| 2 | Stamp plumbing: `DocumentBuffer.Generation`, `FeatureBindingMatchSet.Stamp` | No | — | M |
| 3 | Guard `DocumentBufferService.UpdateTags` | **Yes** | R4 | S |
| 4a | Guard `BindingMatchService.Store` — **log-only** | No (logs and counters only) | — | S |
| 4b | Enforce the `Store` guard; skip follow-on work on rejection | **Yes** | R1, R2, R3 | M |
| 5 | Semantic-token cache and `FeatureTagIndex` keyed by stamp | **Yes** (removes the explicit invalidation) | — | S |
| 6 | *(conditional)* Generation for cross-document reads | Yes | §3.7 | S |
| 7 | Doc reconciliation | No | — | S |

The order is chosen so every step that changes behaviour comes after the plumbing it depends on and
the tests that prove it. Phase 4 is split so that the riskiest rule — rule 1 rejecting a write that
nothing follows up (design §5) — is observed in real sessions before it is enforced.

---

## Phase 0 — Characterisation tests

**Goal:** turn R1–R4 from code-reading findings into failing tests, so the later phases have an
objective "fixed" signal and so we find out early if any of them can't actually happen.

**Scope**

- `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Tagging/GherkinDocumentTaggerServiceRaceTests.cs` (new):
  - **R1:** a fake `IDocumentBufferService` whose `TryGet` returns "not open" for the guard, then
    simulates a `didOpen` + `ParseAsync` before `ScanClosedFileAsync` reaches `Store`. Assert the
    open file's match set survives.
  - **R2:** start `ParseAsync`, then run the `didClose` sequence (`Remove`, `InvalidateAllForDocument`,
    `RescanClosedFileAsync`) before its `Store`. Assert the cache holds the disk-based entry.
  - **R4a:** `ParseAsync` for buffer N, with `Update(N+1)` landing before `UpdateTags`. Assert the
    N+1 buffer's `Tags` are not N's tags.
  - **R4b:** `ParseAsync` with `Remove` landing before `UpdateTags`. Assert no buffer exists afterwards.
- `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Matching/BindingMatchServiceOrderingTests.cs` (new):
  - **R3:** store a closed-file set built against registry v2, then one built against v1, for the same
    key. Assert the v2 set is kept.

All the interleavings are driven **deterministically** through the existing interfaces (fakes that
run a callback at a chosen point), with no thread timing. The tagger already depends only on
interfaces, so no production seams are needed. Follow the "Core tests avoid `StubIdeScope`" rule, so
the net10 Core tests don't load WPF.

Each test asserts the **desired** behaviour and is marked
`[Fact(Skip = "Versioned derived state #906 — fixed in Phase N")]`. Each fixing phase removes its
skips.

**Exit criteria:** each test fails when its `Skip` is temporarily removed. If one passes, the race it
describes can't happen as written. Update design §1.3 and drop that test rather than carrying a fix
for a race that doesn't exist.

**Validation:** `dotnet test` on the two test projects.

---

## Phase 1 — `IDocumentGenerations`

**Goal:** the server keeps a per-URI content generation (design §3.1). Nothing reads it yet.

**Scope**

- `LSP.Core/Documents/IDocumentGenerations.cs` + `DocumentGenerations.cs`:
  - `long Current(string documentId)` and `long Bump(string documentId)`;
  - backed by a case-insensitive `ConcurrentDictionary<string,long>` with `AddOrUpdate`. Increments
    are commutative, so `AddOrUpdate` re-running its update function under contention is harmless.
- Register as a singleton in `Hosting/ServiceCollectionExtensions.cs`.
- Bump sites:
  - `TextDocumentSyncHandler`: `didOpen`, `didChange` (before `Update`), and `didClose` (before
    `Remove`), for `.feature` files only;
  - a feature file leaving the project via a membership delta. `MembershipIndex` currently purges only
    files in the binding role (`ProjectBindingFilesRemovedNotification`), and no path for a feature
    file leaving was found (design I1). Confirm this. If it's a gap, add the bump **and**
    `InvalidateAllForDocument` for removed feature files together, so I1 holds.

**Tests:** unit tests for `DocumentGenerations` (starts at 0, monotonic, kept after close,
case-insensitive), plus sync-handler tests asserting one bump per event.

**Exit criteria:** every event in design §3.1 bumps exactly once. The design's I1 table is confirmed
against the code, and corrected in the design doc if not.

---

## Phase 2 — Stamp plumbing

**Goal:** every per-document derived entry records the stamp of its inputs. Still nothing is
enforced.

**Scope**

- `DerivationStamp` record struct in `LSP.Core/Matching` (design §3.2), with lexicographic ordering
  and `IComparable`.
- `FeatureBindingMatchSet`:
  - adds `DerivationStamp Stamp`;
  - `RegistryVersion` becomes `Stamp.RegistryVersion`;
  - keeps the current constructor and `FromTags` signatures as overloads defaulting the generation to
    0, so the ~40 existing test call sites don't change in this PR.
- `DocumentBuffer` adds `long Generation`. `IDocumentBufferService.Update` takes it.
- Production construction sites:
  - `GherkinDocumentTaggerService.ParseAsync` takes the generation from the buffer it already
    snapshots;
  - `ScanClosedFileAsync` takes it from `IDocumentGenerations.Current` **before** its caller reads the
    disk text. That means passing it in from `BindingRegistryChangedHandler` and
    `RescanClosedFileAsync`, both of which read the file before calling.

**Tests:** stamp ordering unit tests (all four quadrants plus equal); construction sites populate
stamps.

**Exit criteria:** no behaviour change. The full `LSP.Core.Tests` and `LSP.Server.Tests` suites pass
unchanged.

---

## Phase 3 — Guard the buffer's tag write (fixes R4)

**Goal:** tags are attached only to the buffer they were computed from, and a closed buffer is never
brought back.

**Scope**

- `IDocumentBufferService.UpdateTags(uri, generation, tags)` returns `bool`:
  - it is applied only if the buffer exists **and** `buffer.Generation == generation`;
  - it uses an `AddOrUpdate`-free compare-and-swap loop (`TryGetValue` / `TryUpdate`);
  - it never creates a buffer.
- `ParseAsync`: if `UpdateTags` returns `false`, log at verbose and return early. That skips the match
  `Store` and the semantic-token invalidation, because the parse is superseded.

This is enforced directly with no log-only step. A rejected tag write always has a newer parse
already scheduled behind it on the same `ParseCoordinator` chain (`didChange` always schedules one),
or the document is closed, in which case dropping is correct.

**Tests:** remove the skips on R4a and R4b. Add a test that an `UpdateTags` for an unknown URI is a
no-op.

**Exit criteria:** R4 tests pass. Full suites pass. A live smoke test in VS Code — type quickly in a
`.feature` file, close it mid-edit, reopen it — shows correct colouring and no lost CodeLens.

---

## Phase 4a — `Store` guard, log-only

**Goal:** see how often each race really happens, and catch any rejection nothing follows up, before
anything is dropped.

**Scope**

- `BindingMatchService` takes `IDocumentGenerations`. That's a Core-to-Core dependency, so the layer
  boundary is unchanged.
- `Store` works out a `StoreOutcome` per design §3.3, **logs** every rejection at verbose (key,
  incoming and stored stamps, current generation, caller) and **still stores**. `Store` returns the
  outcome it computed; callers ignore it for now.
- `GetCacheStats` gains `AcceptedWrites`, `RejectedSuperseded` and `RejectedStale`. They appear in
  the existing cache-stats log line.
- Log an I3 breach (newer generation with a lower registry version) at warning.

**Observation period:** at least one working week of normal dogfooding across VS and VS Code, plus
one Rider devcontainer session. It must include a large-solution startup (where the startup burst
caused #554) and a branch switch while files are open.

For every logged rejection, check that a later **accepted** write for the same key follows it in the
log. Any rejection without one is an I1 gap: fix it, and re-observe, before 4b.

**Exit criteria:** no unexplained rejections. Counter totals and a sample of log excerpts are posted
on the tracking issue.

---

## Phase 4b — Enforce the `Store` guard (fixes R1–R3)

**Scope**

- `Store` drops rejected writes.
- `ParseAsync` and `ScanClosedFileAsync` check the returned `StoreOutcome`. On rejection they skip
  `InvalidateCache` and anything downstream that a rejected write would otherwise trigger.
- `ProjectBindingRegistry.Invalid` becomes `static readonly` (design Q3).
- Remove the skips on R1, R2 and R3.
- Add a linked-file test (design Q2): one URI, two owners, registries advancing independently.

**Validation:**

- full `LSP.Core.Tests` and `LSP.Server.Tests`;
- the LSP behaviour spec suite;
- `BindingMatchServiceConcurrencyTests` (#554) still green;
- live: the #554 reproduction (startup burst on a solution with linked feature files) shows correct
  step-usage CodeLens counts across three cold starts.

**Rollback:** revert 4b. 4a's logging stays, so a revert still leaves the diagnostics in place.

---

## Phase 5 — Downstream caches keyed by stamp

**Scope**

- `SemanticTokensService`:
  - cache key `(DocumentUri, DerivationStamp)` replaces `(DocumentUri, int)`;
  - `PurgePriorVersions` compares stamps;
  - remove the `_semanticTokenService.InvalidateCache(uri)` call in `ParseAsync`. Its comment
    explains it exists only because tags can change without the client version changing, which the
    registry part of the stamp now covers.
- `FeatureTagIndex`: compare `Generation` instead of client `Version`.

**Tests:**

- a semantic-tokens cache hit for the same stamp;
- a miss when only the registry version changes (this is the case the removed invalidation used to
  cover);
- `FeatureTagIndex` freshness for closed files.

**Exit criteria:** suites green. Live: editing a `.cs` step definition recolours an open `.feature`
file with no edit to the feature file. This is the exact case the removed invalidation guarded.

---

## Phase 6 — *(conditional)* Generation for cross-document reads

Do this only if, **after 4b**, step-usage CodeLens counts or find-usages results can be shown to stay
wrong (not just flicker and then correct) after a rescan. If so, implement the smallest form from
design §3.7: `BindingMatchService.Generation` read before and after a sweep, with one extra
`workspace/codeLens/refresh` when it changed. If no such evidence turns up within a release after 4b,
close this phase as not needed and record that on the tracking issue.

---

## Phase 7 — Doc reconciliation

- Mark the design **IMPLEMENTED** and add an "as built" section noting where the code departed from
  the design.
- Update the cache and pipeline sections of `docs/LSP-IDE-Support-Architecture.md`. (That document
  is already known to have drifted, #520, so update only the parts this work touched.)
- Delete the Phase 0 `Skip` markers if any remain.

---

## Out of scope — separate follow-ups

- **Watch `.feature` files on disk** (design Q1). Today a closed feature file edited outside the IDE
  isn't rescanned until the next full registry replacement. If this is added, it must bump the
  generation and schedule a rescan together.
- **Cross-document snapshot index** beyond Phase 6's minimal form.
- **Any CQRS-style restructuring of handlers.** The review behind this plan concluded it isn't
  warranted. One related change worth doing on its own is an architecture test enforcing "`Features/`
  query handlers don't depend on write-side services". It's independent of this plan and doesn't
  block it.
