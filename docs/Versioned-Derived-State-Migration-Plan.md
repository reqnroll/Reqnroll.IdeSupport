# Versioned Derived State — Migration Plan

**Status: PROPOSED (2026-10-05), revised 2026-11-14.** Tracking issue: #906. Design:
[Versioned-Derived-State-Design.md](Versioned-Derived-State-Design.md). Section references (§) are to
that document; R1–R6 are the stale-write paths in its §1.3 (R4b is already fixed, so R4 here means
R4a).

**Before Phase 0:** this branch was written against a much older `main`. Rebase it (or start Phase 0
from a fresh branch off `main`) and re-read §1.2 against the code once more; the writer list is the
part most likely to drift.

This plan says what to build, in what order, and what "done" means for each step. Each phase is
**one PR**: it can ship on its own, it leaves `main` releasable, and it can be undone with a plain
revert. Nothing changes on the LSP wire or in any client, so the VS, VS Code and Rider extensions are
untouched throughout.

---

## Phase overview

| Phase | What | Changes behaviour? | Fixes | Size |
|---|---|---|---|---|
| 0 | Characterisation tests for R1–R3, R4a, R5, R6 (skipped until fixed) | No | — | S |
| 1 | `IDocumentGenerations` + `IProjectConfigGenerations` + bump wiring | No (nothing reads them yet) | — | S |
| 1b | Rescan closed feature files after a rename edits them | **Yes** | R5 (missing re-derivation) | S |
| 2 | Stamp plumbing: `DocumentBuffer.Generation`, `FeatureBindingMatchSet.Stamp` | No | — | M |
| 3 | Generation check in `DocumentBufferService.UpdateTags` | **Yes** | R4a | S |
| 4a | Guard `BindingMatchService.Store` — **log-only** | No (logs and counters only) | — | S |
| 4b | Enforce the `Store` guard; skip follow-on work on rejection | **Yes** | R1, R2, R3, R5 (late write), R6 | M |
| 5 | Semantic-token cache and `FeatureTagIndex` keyed by stamp | **Yes** (removes the explicit invalidation) | — | S |
| 6 | *(conditional)* Generation for cross-document reads | Yes | §3.7 | S |
| 7 | Doc reconciliation | No | — | S |

The order is chosen so every step that changes behaviour comes after the plumbing it depends on and
the tests that prove it. Phase 4 is split so that the riskiest rule — rule 1 rejecting a write that
nothing follows up (design §5) — is observed in real sessions before it is enforced.

---

## Phase 0 — Characterisation tests

**Goal:** turn R1–R3, R4a, R5 and R6 from code-reading findings into failing tests, so the later
phases have an objective "fixed" signal and so we find out early if any of them can't actually happen.

**Scope**

- `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Tagging/GherkinDocumentTaggerServiceRaceTests.cs` (new),
  next to the existing `GherkinDocumentTaggerServiceCloseRaceTests` (which already covers R4b; don't
  duplicate it):
  - **R1:** a fake `IDocumentBufferService` whose `TryGet` returns "not open" for the guard, and a
    fake tag parser that, during the closed scan's parse, simulates a `didOpen` + `ParseAsync`. Assert
    the open file's match set survives the closed scan's `Store`.
  - **R2:** the narrowed window left after #938. A fake `IDocumentBufferService` whose `UpdateTags`
    returns `true` and then, before returning, runs the `didClose` sequence (`Remove`,
    `InvalidateAllForDocument`, `RescanClosedFileAsync`). Assert the cache holds the disk-based entry
    after `ParseAsync`'s `Store`.
  - **R4a:** `ParseAsync` for buffer N, with `Update(N+1)` landing after the version check and before
    `UpdateTags` (inject it from the fake tag parser). Assert the N+1 buffer's `Tags` are not N's tags.
  - **R6:** two closed scans of the same file against the same registry, the first with the old
    feature language and the second with the new one, where the old-language scan stores last. Assert
    the new-language set is kept.
  - **R5b:** a closed scan that read the pre-rename text stores after the rename's invalidation and
    the post-rename rescan. Assert the post-rename entry is kept. It lives here rather than with the
    rename tests because it's about the order writes reach the store; the hook replays what the rename
    coordinator does. (Design Q6: the overlap could be driven deterministically.)
- `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/Rename/RenamePostApplyCoordinatorTests.cs`
  (existing):
  - **R5a:** after a rename edits a closed feature file, assert its match set is repopulated (not just
    invalidated).
- `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Matching/BindingMatchServiceOrderingTests.cs` (new):
  - **R3:** store a closed-file set built against registry v2, then one built against v1, for the same
    key. Assert the v2 set is kept.

All the interleavings are driven **deterministically** through the existing interfaces (fakes that
run a callback at a chosen point), with no thread timing. The tagger already depends only on
interfaces, so no production seams are needed. Follow the "Core tests avoid `StubIdeScope`" rule, so
the net10 Core tests don't load WPF.

Each test asserts the **desired** behaviour and is marked
`[Fact(Skip = "Stale derived-state write is accepted until versioned-derived-state Phase N")]`. Don't
put the issue number in the skip text or in test comments: code describes what it does, not which
issue asked for it. Each fixing phase removes its skips.

**Exit criteria:** each test fails when its `Skip` is temporarily removed. If one passes, the race it
describes can't happen as written. Update design §1.3 and drop that test rather than carrying a fix
for a race that doesn't exist.

**Validation:** `dotnet test` on the two test projects.

---

## Phase 1 — `IDocumentGenerations`

**Goal:** the server keeps a per-URI content generation and a per-project config generation (design
§3.1). Nothing reads them yet.

**Scope**

- `LSP.Core/Documents/IDocumentGenerations.cs` + `DocumentGenerations.cs`:
  - `long Current(string documentId)` and `long Bump(string documentId)`;
  - backed by a case-insensitive `ConcurrentDictionary<string,long>` with `AddOrUpdate`. Increments
    are commutative, so `AddOrUpdate` re-running its update function under contention is harmless.
- `IProjectConfigGenerations` + `ProjectConfigGenerations` in `LSP.Core`: the same shape, keyed by
  `ProjectOwner`.
- Register both as singletons in `Hosting/ServiceCollectionExtensions.cs`.
- Bump sites:
  - `TextDocumentSyncHandler`: `didOpen`, `didChange` (before `Update`), and `didClose` (before
    `Remove`), for `.feature` files only;
  - `RenamePostApplyCoordinator.InvalidateClosedFeatureCaches`: each closed feature file it edits,
    before the invalidation;
  - `ReqnrollConfigChangedHandler`: the changed project's config generation, before it publishes
    `BindingRegistryPatchedNotification`. Its workspace-root path (`.editorconfig` changes) doesn't
    bump: `.editorconfig` never sets the feature language.

A feature file leaving one project via a membership delta is **not** a bump site (design §3.1 and
Q7). Confirmed during Phase 1 that the delta leaves a stale `(uri, project)` match set; that's a
separate per-key fix, not part of this phase.

**Tests:** unit tests for `DocumentGenerations` (starts at 0, monotonic, kept after close,
case-insensitive) and `ProjectConfigGenerations`, plus handler tests asserting one bump per event
(sync handler, rename coordinator, config handler).

**Exit criteria:** every event in design §3.1 bumps exactly once. The design's I1 and I4 lists are
confirmed against the code, and corrected in the design doc if not.

---

## Phase 1b — Re-derive closed feature files after a rename (fixes R5's missing re-derivation)

**Goal:** a closed feature file edited by a rename stays visible to Find Usages, usage counts and
later renames, instead of dropping out of the cache until the next project scan.

**Decided (built in #1349):** derive from the post-rename text computed **at staging**, not from
the disk at commit. When the client confirms, the disk can't be trusted either way: a client may
have applied the edits to the closed file in memory without saving (disk still old), or saved them
(applying the edits again would apply them twice). At staging the rename response hasn't gone out,
and the disk holds exactly the text the edits were built against. This mirrors how the coordinator
already handles the edited `.cs` file (`newCsText`).

**Scope (as built)**

- `TextEditApplier` (new): applies LSP text edits to a string per the specification (line breaks,
  past-line-end characters, same-position inserts); `null` for edits that don't fit or overlap.
- `RenamePostApplyCoordinator.StagePendingCommit` reads each touched feature file that is closed
  and stores its post-rename text with the pending commit. A file that can't be read, or whose edits
  don't fit, is only invalidated on commit, as before.
- On commit: refresh the C# bindings **first** (otherwise the renamed steps are matched against the
  old binding and come out undefined), then for each closed feature file bump its generation,
  invalidate it, and re-derive it for every owner through the new
  `IGherkinDocumentTaggerService.ScanClosedFileForAllOwnersAsync(uri, text)`.
  `RescanClosedFileAsync` reads the file and delegates to it.

This doesn't depend on Phases 2–4 and can ship any time after Phase 0. It's a fix of its own even
if the rest of the plan stops.

**Tests:** coordinator tests cover the exact renamed text, the staged text surviving a client save,
binding-refresh order, the invalidate-only fallbacks, open files and rejected renames, plus an end-to-end
test through the real tagger and match store. The Phase 0 R5a test assumed a disk rescan (it writes
the renamed text to disk and uses a dummy edit); rewrite it to stage the pre-rename text with a real
edit, then remove its skip.

**Exit criteria:** R5a passes. Live: rename a step from a `.cs` file with a closed feature file that
uses it, then run Find Usages on the renamed binding; the closed file is listed.

---

## Phase 2 — Stamp plumbing

**Goal:** every per-document derived entry records the stamp of its inputs. Still nothing is
enforced.

**Scope**

- `DerivationStamp(ContentGeneration, ConfigGeneration, RegistryVersion)` record struct in
  `LSP.Core/Matching` (design §3.2), with lexicographic ordering and `IComparable`.
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
    `RescanClosedFileAsync`, both of which read the file before calling;
  - both read `IProjectConfigGenerations.Current` for the owner before
    `ProjectFeatureLanguage.For(...)`. In `ParseAsync` that's the primary owner, which is also the
    match-set key's owner.

**Tests:** stamp ordering unit tests (each component deciding on its own, mixed cases, equal);
construction sites populate stamps.

**Exit criteria:** no behaviour change. The full `LSP.Core.Tests` and `LSP.Server.Tests` suites pass
unchanged.

---

## Phase 3 — Generation check on the buffer's tag write (fixes R4a)

**Goal:** tags are attached only to the buffer they were computed from.

Most of the original phase already shipped with #938: `UpdateTags` is update-only (a
`TryGetValue` / `TryUpdate` loop), returns `bool`, never creates a buffer, and `ParseAsync` drops its
result when it returns `false`. What's left:

**Scope**

- `IDocumentBufferService.UpdateTags(uri, generation, tags)`: inside the existing loop, also return
  `false` when `existing.Generation != generation`.
- `ParseAsync`: passes the generation from its snapshot. The existing `false` branch already skips
  the match `Store` and the semantic-token invalidation; update its log text so it covers "superseded"
  as well as "closed".

This is enforced directly with no log-only step. A rejected tag write always has a newer parse
already scheduled behind it on the same `ParseCoordinator` chain (`didChange` always schedules one),
or the document is closed, in which case dropping is correct.

**Tests:** remove the skip on R4a. `GherkinDocumentTaggerServiceCloseRaceTests` and the existing
"unknown URI is a no-op" coverage must stay green.

**Exit criteria:** R4a passes. Full suites pass. A live smoke test in VS Code — type quickly in a
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

## Phase 4b — Enforce the `Store` guard (fixes R1–R3, R5's late write, R6)

**Scope**

- `Store` drops rejected writes.
- `ParseAsync` and `ScanClosedFileAsync` check the returned `StoreOutcome`. On rejection they skip
  `InvalidateCache` and anything downstream that a rejected write would otherwise trigger.
- `ProjectBindingRegistry.Invalid` becomes `static readonly` (design Q3).
- Remove the skips on R1, R2, R3, R5b and R6.
- Add a linked-file test (design Q2): one URI, two owners, registries advancing independently.

**Validation:**

- full `LSP.Core.Tests` and `LSP.Server.Tests`;
- the LSP behaviour spec suite;
- `BindingMatchServiceConcurrencyTests` (#554) still green;
- live: the #554 reproduction (startup burst on a solution with linked feature files) shows correct
  step-usage CodeLens counts across three cold starts;
- live: change `language.feature` in a project's `reqnroll.json` while a rebuild is running; closed
  feature files in the new language report correct usage counts once both settle.

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
- `FeatureTagIndex`: compare `Generation` instead of client `Version`, and `ConfigGeneration`
  instead of the feature language it records today.

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
