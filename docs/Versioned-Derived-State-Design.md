# Versioned Derived State — Design

**Status: PROPOSED (2026-10-05), revised 2026-11-14.** Tracking issue: #906. Migration plan:
[Versioned-Derived-State-Migration-Plan.md](Versioned-Derived-State-Migration-Plan.md). What the
revision changed is listed in §6.

This is a design record for how the LSP server keeps the state it derives from `.feature` and `.cs`
files (match sets, tags, semantic-token cache, tag index) from being overwritten by stale results.
It came out of a review of whether the LSP.Server / LSP.Core codebase should move to CQRS. The
conclusion was **no wholesale CQRS refactor**: the protocol and the existing MediatR pipeline already
separate commands (`didOpen`/`didChange`/`didChangeWatchedFiles`/`reqnroll/*` notifications) from
queries (hover, definition, completion, semantic tokens, CodeLens, references). The one CQRS idea
worth adopting deliberately is that **read models carry the versions of their inputs, and writers
refuse to replace a newer read model with an older one.**

Related: #554 (non-atomic `Store` race — fixed by a lock, not by ordering), #517 (`.cs` reparse race),
#471 (`ParseCoordinator` / Serial-lane offload), #477 (fire-and-forget registry publish),
#577 (Replaced vs Patched registry events), #580 (bounded closed-file scan),
#469 / #515 (duplicate bindings), #938 (late parse re-created a closed buffer — fixed, see R4b).

---

## 1. Problem

### 1.1 The stamps exist but are never read

| Artifact | Version information today | Enforced? |
|---|---|---|
| `DocumentBuffer` (`LSP.Server/Documents`) | `int? Version` — client's document version | No: `Update` overwrites unconditionally. `UpdateTags` is update-only (it never creates a buffer) but tags whichever buffer is there now |
| Project configuration (feature language) | none | n/a — read at derivation time with `ProjectFeatureLanguage.For(project)`; nothing records which configuration a derived entry used |
| `ProjectBindingRegistry` (`LSP.Core/Bindings`) | `int Version` — process-wide monotonic counter, new value per immutable instance | n/a (instances are immutable; swapping is the provider's job) |
| `FeatureBindingMatchSet` (`LSP.Core/Matching`) | `int? DocumentVersion`, `int RegistryVersion` | **No.** Nothing in `LSP.Core` or `LSP.Server` reads either property |
| `BindingMatchService` reverse/location indexes | none (derived from the stored match sets) | n/a |
| `SemanticTokensService` cache | keyed `(DocumentUri, clientVersion)` | Partially: `PurgePriorVersions`, plus an explicit `InvalidateCache` after every parse because tags can change without the client version changing |
| `FeatureTagIndex` | compares cached `Version` against `buffer.Version` on read | Yes, on the read side only |

`BindingMatchService.Store` takes the write lock (#554) and then replaces whatever is under the
`(DocumentId, Owner)` key: **the last write wins, not the newest.** The #554 fix made each write
atomic, but it doesn't order writes.

### 1.2 Who writes derived per-document state

| Writer | Path | Serialised by `ParseCoordinator`? |
|---|---|---|
| `GherkinDocumentTaggerService.ParseAsync` via `FeatureDocumentReparser` | `didOpen` / `didChange` (`TextDocumentSyncHandler`), `DocumentActivatedHandler`, `ReqnrollConfigChangedHandler` (workspace-root fallback), `BindingRegistryChangedHandler` (open files) | **Yes** — per-URI chain |
| `GherkinDocumentTaggerService.ScanClosedFileAsync` | `BindingRegistryChangedHandler.ScanAllFeatureFilesAsync`: run inline for a full replacement, and through the rescan debouncer for a patch (which also covers a `reqnroll.json` change, since `ReqnrollConfigChangedHandler` publishes `BindingRegistryPatchedNotification` for the changed project) | **No** |
| `GherkinDocumentTaggerService.RescanClosedFileAsync` | `didClose` (inline on the Serial lane) | **No** |
| `BindingMatchService.InvalidateAllForDocument` / `InvalidateAllForProject` | `didClose`; project removal (`BindingRegistryProviderRouter.OnProjectRemoved`); closed feature files edited by a rename (`RenamePostApplyCoordinator.InvalidateClosedFeatureCaches`) | **No** |

The registry-change events themselves are published with `FireAndForget` from
`BindingRegistryProviderRouter.OnProviderChanged` (#477). Two registry changes for the same project in
quick succession therefore run `BindingRegistryChangedHandler` **concurrently**, with nothing ordering
them. Three triggers can each start a closed-file scan of the same project: a full replacement, the
debounced rescan after a patch, and a `reqnroll.json` change (which goes through the patch path).

`ParseAsync` already drops its result when `UpdateTags` reports that the buffer is gone (#938). It
then skips both the match-set `Store` and the semantic-token invalidation.

### 1.3 Concrete stale-write paths

All of these were found by reading the code, and rechecked against `main` on 2026-11-14. **None of
the open ones has been reproduced yet**; Phase 0 of the migration plan writes deterministic tests for
each.

- **R1 — Closed-file scan overwrites an open file.** `ScanClosedFileAsync` checks
  `_documentBufferService.TryGet(uri)` and skips the scan if the file is open. That's a check-then-act:
  a `didOpen` can land between the check and `Store`. The closed scan then replaces the open file's
  match set with one built from disk text (`documentVersion: null`), possibly against an older
  registry. The method's own comment says the guard only narrows the race.
- **R2 — A parse started before `didClose` stores after it.** The `didClose` handler removes the
  buffer, calls `InvalidateAllForDocument`, then rescans from disk. It doesn't wait for or cancel a
  parse already scheduled for the URI on `ParseCoordinator`. Since #938, a parse that reaches
  `UpdateTags` after `Remove` drops its result, which closes most of the original window. What's left
  is this order: the parse's `UpdateTags` succeeds (the buffer still exists), then the whole close
  sequence runs, then the parse reaches `Store`. It then puts back a match set built from the editor
  text, which may have been discarded, over the disk-based entry.
- **R3 — Two closed-file scans of one project race.** Each file's scan reads `provider.Current` when
  it reaches that file, not once per loop. So the race isn't between two whole loops: for one file,
  scan A reads registry vN and is still parsing when scan B reads vN+1 and stores; then A stores the
  vN result. The cache then holds matches against the old bindings until the next scan of that file.
  The window is one file's parse, but there are three triggers that can overlap (§1.2).
- **R4 — Tags from an older buffer get attached to a newer one.**
  - **R4a (open).** `didChange` calls `DocumentBufferService.Update` inline. That replaces the
    buffer and clears `Tags`. The parse runs later on the coordinator chain. `ParseAsync` checks the
    version only at its start. If `Update(N+1)` lands after that check but before parse N's
    `UpdateTags`, N's tags are attached to the N+1 buffer. They stay there until parse N+1 finishes,
    which the coordinator schedules right after. Impact is short-lived wrong folding, outline or
    colouring for open-buffer readers.
  - **R4b (fixed, #938).** `UpdateTags` used `AddOrUpdate`, so a parse finishing after `didClose`
    re-created the buffer as a ghost "open" document. `UpdateTags` is now update-only and returns
    `false` when the buffer is gone; `GherkinDocumentTaggerServiceCloseRaceTests` cover it.
- **R5 — A rename edits a closed feature file without a re-derivation.**
  `RenamePostApplyCoordinator.InvalidateClosedFeatureCaches` invalidates the match sets of closed
  feature files the rename edited. It doesn't rescan them, so they drop out of Find Usages, usage
  counts and later renames until the next scan of their project. A closed-file scan that read the
  old disk text before the edit can also store after the invalidation. Today that brings the old
  step text back; under this design it would be accepted because nothing bumped the generation. The
  "is it open?" check in that method has the same check-then-act shape as R1.
- **R6 — Configuration isn't an input anyone records.** A `reqnroll.json` change (for example
  `language.feature`) changes how every feature file of the project parses. It's reconciled through
  the patch path (§1.2), which re-derives every file with the same content and, if the assembly
  didn't change, the same registry. A scan that read the old configuration can therefore store after
  one that read the new configuration, and nothing tells them apart. A stamp of only
  `(contentGeneration, registryVersion)` would see equal stamps and accept both, so it wouldn't fix
  this.

R1–R3, R5 and R6 produce wrong usage counts, find-usages results and rename coverage. Those features
read the cache across documents, so a wrong entry stays until the next re-derivation of that file.

## 2. Goals and non-goals

**Goals**

- G1. A write of derived state built from older inputs never replaces one built from newer inputs,
  whatever order the writers finish in.
- G2. The rule is checked where the state is stored, so a new writer path can't skip it by mistake.
- G3. Open and closed files follow the same rule.
- G4. Ordering becomes testable without threads: storing stale-after-fresh in a unit test must leave
  the fresh entry.
- G5. No LSP protocol change, no client change.

**Non-goals**

- A Roslyn-style immutable `Solution` snapshot of the whole workspace.
- Removing `ParseCoordinator`. Per-URI serialisation stays. It prevents wasted concurrent parses and
  gives `WaitForReadyAsync` its meaning. Versioning covers the paths it doesn't serialise.
- Event sourcing, separate read/write stores, or a command bus.
- Cross-document consistency of the reverse index. That is deferred to an optional phase (§3.7).

## 3. Design

### 3.1 Version sources

| Source | Scope | Owner | Status |
|---|---|---|---|
| **Content generation** `long` | per document URI | new `IDocumentGenerations` in `LSP.Core/Documents` | **new** |
| **Config generation** `long` | per project | new `IProjectConfigGenerations` in `LSP.Core` | **new** (R6) |
| Registry version `int` | per `ProjectBindingRegistry` instance, unique across the process | `ProjectBindingRegistry.Version` | existing |
| Client document version `int?` | per open document | LSP client | existing — kept only for wire messages (`publishDiagnostics.version`, `WorkspaceEdit`) |

**Why a server-side content generation instead of the client version.** The client version doesn't
exist for closed files, and it resets when a file is reopened. The R1 and R2 races are exactly about
moving between open and closed. A per-URI counter owned by the server goes up on every event that
changes what the server believes the text is:

- `textDocument/didOpen`, `didChange`, `didClose` (a close switches the source of truth from the
  editor buffer to the file on disk);
- a rename that edits a **closed** feature file (`RenamePostApplyCoordinator`). Open files edited by a
  rename already get a `didChange`, which bumps (R5).

A feature file leaving **one** project (a membership delta via `reqnroll/projectFiles`) is **not** a
bump site, although an earlier draft listed it. The text didn't change, and the generation is per
document, not per `(document, project)`: bumping it would reject in-flight writes for the file's
*other* owners, and nothing would re-derive them, which breaks I1. The removal leaves a stale
`(uri, project)` match set behind today (confirmed 2026-11-14: `MembershipIndex` publishes a purge
only for binding-role files). That is a separate fix: drop the one `(uri, project)` entry when the
delta removes the feature file. See §5 Q7.

**Config generation.** A per-project counter, bumped by `ReqnrollConfigChangedHandler` **before**
it publishes the reconcile for that project. Derivations read it before they read the project's
feature language. It sits next to the registry version because both describe "the project", not
"the document"; it's separate because a configuration change doesn't produce a new registry when
the assembly is unchanged (R6). The handler's workspace-root path (no project on the notification)
doesn't bump: it serves `.editorconfig` changes, and the feature language comes only from
`reqnroll.json` or the SpecFlow configuration, never from `.editorconfig`.

The server doesn't watch `.feature` files on disk today: `WatchedFilesHandler` handles only config,
`.editorconfig`, output assemblies and deleted `.cs` files. So a closed feature file changed outside
the editor (for example by `git checkout`) doesn't bump the generation, and isn't rescanned until the
next full registry replacement. That's an **existing gap, not introduced here** (see §5 Q1). Under
§3.3 its rescans are accepted as equal-generation writes, so this design doesn't make it worse.

The counter is created on first use, never decreases, and **is kept after `didClose`**. Keeping it
is what lets a late write from before the close be recognised as stale. Memory cost is one
`string → long` entry per feature URI ever seen. That is the same order as the existing match cache,
which already holds an entry for every feature file in the workspace.

### 3.2 The stamp

Every per-document derived entry records a `DerivationStamp`:

```csharp
public readonly record struct DerivationStamp(
    long ContentGeneration, long ConfigGeneration, int RegistryVersion)
    : IComparable<DerivationStamp>
{
    // Lexicographic: newer text always wins; for the same text, the newer configuration wins;
    // for the same text and configuration, the newer registry wins.
    public int CompareTo(DerivationStamp other) => ...;
}
```

Every component is **read before the input it describes**:

- `ContentGeneration`: for an open file, captured together with the buffer snapshot; for a closed
  file, captured before `ReadAllTextAsync`;
- `ConfigGeneration`: captured before `ProjectFeatureLanguage.For(project)`;
- `RegistryVersion`: taken from the registry instance actually used for matching.

This prevents a stamp that is newer than the input it describes.

**Why lexicographic order and not "dominates on both".** With a strict "both components ≥" rule,
`(g6, r8)` and `(g5, r9)` are incomparable. Whatever tie-break you pick, the next incomparable write
can flip it back. A total order avoids that. Lexicographic order is correct as long as these hold:

- **I1 — every content bump is followed by a re-derivation of the new content.** `didOpen`,
  `didChange` → scheduled parse. `didClose` → `RescanClosedFileAsync`. Rename of a closed file →
  `RescanClosedFileAsync` (**gap today**: it only invalidates, R5; Phase 1b adds the rescan).
  Membership removal isn't a content bump (§3.1).
- **I2 — every registry change re-derives that project's entries.** Replaced → every file. Patched →
  open files immediately, closed files after the rescan debounce. (Existing behaviour, #577.)
- **I3 — project-input reads are monotonic per project.** A derivation that starts later never sees
  an older registry or an older config generation than one that started earlier. (Holds for the
  registry today: `provider.Current` is one reference replaced by a newer instance. Holds for the
  config generation by construction: a counter that only goes up.)
- **I4 — every config bump is followed by a re-derivation of the project's entries.** Holds today:
  `ReqnrollConfigChangedHandler` publishes `BindingRegistryPatchedNotification`, which reparses open
  files and schedules the debounced closed-file rescan.

Given I3, a derivation that starts later has every component equal or higher, so lexicographic order
and "dominates on every component" agree in practice; the order of the components only matters as a
tie-break that shouldn't arise. If a write is newer on an earlier component but *lower* on a later
one, the store logs a warning ("I3 violated") rather than silently accepting it.

### 3.3 Write rule

A guarded store (`BindingMatchService.Store`, `DocumentBufferService.UpdateTags`) accepts an incoming
entry `e` for key `k` only if **both** hold:

1. **Not superseded:** `e.Stamp.ContentGeneration == generations.Current(k.DocumentId)`. If the
   document has changed since the input was read, drop the write. This check covers R2 and R5, where
   the entry the late write would overwrite has been invalidated, so there's nothing to compare
   against. (It would also have covered R4b, already fixed by making `UpdateTags` update-only.)
2. **Not stale:** there's no stored entry, or `e.Stamp >= stored.Stamp`. This covers R1, R3, R6 and
   R4a.

Equal stamps are accepted. Two scans of identical inputs are interchangeable. A closed file can also
change on disk without a watched-file notification, so the second scan may have newer disk text
under the same generation.

Rule 1 isn't atomic with respect to the generation bump: a bump can land between the check and the
write. The entry stored in that window has the old generation, and I1 guarantees a re-derivation for
the new generation is already scheduled. That re-derivation passes rule 2 and replaces it. The window
corrects itself.

`Store` returns a `StoreOutcome` (`Accepted`, `RejectedSuperseded`, `RejectedStale`) instead of
`void`. On rejection, `ParseAsync` / `ScanClosedFileAsync` skip their follow-on work: the
semantic-token invalidation and the `MatchCacheChangedNotification` publish.

### 3.4 Granularity

- **Per-document entries** (match set, tags, semantic-token cache entry) carry their **input
  stamps**, not a version of their own. They are built from exactly one document plus one registry.
  An extra version would hide which input was stale.
- **The registry** keeps **its own** version, because it is built from many `.cs` files plus the
  compiled assembly and no single input version describes it. No change.
- **Whole collections** (the reverse index and location index inside `BindingMatchService`,
  `FeatureTagIndex`) get **no version** in the main design. They are rebuilt from the
  per-document entries, so they are as correct as those entries. Snapshotting a collection (§3.7) is
  deferred until there is evidence it is needed.

### 3.5 Where the rule applies

| Store | Change |
|---|---|
| `IDocumentGenerations` (new, `LSP.Core/Documents`) | `long Current(string documentId)`, `long Bump(string documentId)`. Implemented with a `ConcurrentDictionary<string,long>`, case-insensitive like `DocumentBufferService`. Singleton. |
| `DocumentBuffer` | adds `long Generation`. `Update(uri, version, generation, text)`. |
| `IProjectConfigGenerations` (new, `LSP.Core`) | `long Current(ProjectOwner)`, `long Bump(ProjectOwner)`. Same shape as `IDocumentGenerations`. Bumped by `ReqnrollConfigChangedHandler`. Fixes R6 together with the `Store` rule. |
| `DocumentBufferService.UpdateTags` | Already update-only and returns `bool` (#938). Becomes `bool UpdateTags(uri, generation, tags)`: applies only if `buffer.Generation == generation`. Fixes R4a. |
| `RenamePostApplyCoordinator.InvalidateClosedFeatureCaches` | Bumps the generation of each closed feature file it edits, then invalidates and calls `RescanClosedFileAsync` for it. Fixes R5's missing re-derivation; the `Store` rule fixes its late-write case. |
| `FeatureBindingMatchSet` | adds `DerivationStamp Stamp`. `DocumentVersion` stays (diagnostics need it on the wire). `RegistryVersion` becomes `Stamp.RegistryVersion`. |
| `BindingMatchService.Store` | the rule from §3.3. Returns `StoreOutcome`. Counts accepted and rejected writes in `GetCacheStats`. |
| `BindingMatchService.InvalidateAllForDocument` | no change. The generation bump that comes before it (on `didClose`, a rename of a closed file) is what rejects late writes. |
| `SemanticTokensService` cache | key becomes `(uri, Stamp)` instead of `(uri, clientVersion)`. The explicit `InvalidateCache` call in `ParseAsync` becomes unnecessary and is removed. `PurgePriorVersions` compares stamps. |
| `FeatureTagIndex` | compares `Generation` instead of the client `Version` (same logic, and it now works for closed files). It already records the feature language each file's counts were parsed with; that check can move to `ConfigGeneration`. |

### 3.6 Relationship to `ParseCoordinator`

The two complement each other. `ParseCoordinator` decides **when** work for one URI runs: it chains
parses for that URI so they don't run concurrently. Stamps decide **whether a finished result may be
stored**, whatever path produced it.

Closed-file scans and registry-driven bulk scans stay outside the coordinator. Routing a
several-thousand-file full scan through per-URI chains would make it wait on every open file's edit
queue. With stamps it doesn't need to be serialised to be correct.

### 3.7 Optional: generation for cross-document reads

Readers that add up over many entries (`FindUsages`, `GetAll` → step-usage CodeLens, Find Unused Step
Definitions, rename planning) take the read lock per call. A CodeLens sweep issues many calls, so it
can see some files before and some after a rescan. Versioning entries doesn't fix this: each entry is
correct, but the set is mixed.

If that turns out to be visible to users (CodeLens counts that are wrong *and stay wrong*, as opposed
to flickering and then correcting via `workspace/codeLens/refresh`), the smallest fix is:

- `BindingMatchService` exposes a `long Generation` that goes up on every accepted write or
  invalidation;
- a sweep reads it before and after;
- if it changed, the sweep requests one more refresh.

A full immutable-snapshot index (`ImmutableDictionary` swap) is the heavier alternative. Both are out
of scope until Phase 4's counters and logs show the problem.

### 3.8 Observability

- Each rejection logs at **verbose**, including the key, the incoming and stored stamps, the current
  generation, and the writer (`ParseAsync` / `ScanClosedFile`).
- `GetCacheStats` gains `AcceptedWrites`, `RejectedSuperseded` and `RejectedStale`, so the existing
  cache-stats logging shows how often each race actually happens.
- A breach of I3 logs a **warning**.

No new telemetry event. If the counters show that races matter in practice, a sampled telemetry
property can be added later through the normal telemetry-inventory process.

## 4. Alternatives considered

| Alternative | Why not |
|---|---|
| **Formal CQRS** (command/query markers, a dispatcher, one handler per operation) | Mostly renames what is already there. OmniSharp already dispatches requests and notifications through MediatR. It doesn't address any of R1–R6. |
| **Route every writer through `ParseCoordinator`** | Solves R1/R2/R4a/R5 for one URI but not R3 or R6: those races are between two *project-level* events (registry or configuration), not two edits of one file. It also puts bulk scans behind interactive edits. |
| **Fix each path locally** (have `didClose` wait for the URI's pending parse; compare client versions in `UpdateTags`; rescan after rename) | Fixes R2, R4a and R5's missing rescan, and is cheaper. It can't fix R1, R3 or R6: those are about writes from *different* paths or *different* triggers to the same key, and only a check at the store can order them. The rename rescan is still worth doing on its own (Phase 1b). |
| **Componentwise dominance instead of lexicographic order** | Leaves an "incomparable" case whose tie-break can flip back and forth (§3.2). |
| **Client version plus a "closed" marker instead of a server generation** | Client versions restart when a file is reopened, so a reopened file's version 1 compares as older than the previous session's version 40. Fixing that requires a server-side counter anyway. |
| **Whole-workspace immutable snapshot** (Roslyn `Solution` model) | Every keystroke would look like it invalidated everything unless we also build Roslyn-style tracking of which parts changed. Too large for the problem. |
| **Upgrade MediatR to use pipeline behaviours for ordering** | We're pinned to 8.x by OmniSharp 0.19.9, and newer MediatR versions are commercially licensed. Ordering is a property of the store, not of dispatch. |

## 5. Risks and open questions

- **Q1. Disk changes to closed `.feature` files are not observed (answered by inspection).**
  `WatchedFilesHandler` has no `.feature` branch, so I1 can't depend on watched-file events. This
  design bumps only on events the server does see, so it is safe as it stands. Watching `.feature`
  files is a separate improvement and should be tracked in its own issue. If it is added later, its
  handler must bump the generation **and** schedule a rescan together, or it would break I1.
- **Q2. Linked or shared feature files.** One URI has several `(uri, owner)` keys but one content
  generation. That's correct, since the text is the same. Registry versions are compared only
  within one key, so each owner is compared against its own project's registry history. Phase 4
  tests should cover a linked file explicitly.
- **Q3. `ProjectBindingRegistry.Invalid`** is a `public static` field that isn't `readonly`. Its
  version is fixed at first use, so it's lower than every real registry and loses every comparison,
  which is the right behaviour. Making it `readonly` is a small cleanup bundled into Phase 4.
- **Q4. `ParseAsync(uri, version)` already bails when `snapshot.Version != version`.** That check
  runs at the *start* of the parse, not at the write. It stays; the rule in §3.3 supersedes it in
  practice. The same goes for its "buffer gone" bail-out after `UpdateTags` (#938).
- **Q5. Other configuration inputs.** Only the feature language was found to change how a feature
  file is derived. If another project setting starts to affect parsing or matching, it must bump the
  config generation through the same handler, or I4 breaks.
- **Q6. Is R5's scan-then-rename overlap real?** It needs a closed-file scan of the project to be in
  progress while a rename is applied. Phase 0 tests it; if it can't be driven deterministically, the
  rescan in Phase 1b still fixes the user-visible part (files dropping out of Find Usages).
- **Q7. A feature file leaving one project keeps that project's match set.** The membership delta
  publishes nothing that drops `(uri, project)`, so the removed file's steps keep counting as that
  project's usages until the next full scan. The fix is per key, not per document (a linked file may
  still belong to other projects), so `IBindingMatchService` needs a single-key invalidation. A late
  write for the removed key could still bring it back; once Phase 4b is in, that write can be
  rejected by also checking membership at the writer, or accepted as harmless until the next scan.
  This is independent of the stamps and can be fixed and shipped on its own.
- **Risk: a wrongly rejected write leaves a cached entry missing or stale.** This is the main risk:
  an I1 gap means rule 1 drops a write and nothing follows it up. That's why Phase 4 ships in
  **log-only mode** first. Counters and logs over real sessions show whether any rejection was
  unexpected before enforcement is switched on.

## 6. Revision 2026-11-14

Rechecked against `main` at 352a2608. The core finding holds: nothing reads
`FeatureBindingMatchSet.DocumentVersion` or `RegistryVersion`, and `BindingMatchService.Store` is
still last-writer-wins. Changes to this document:

- **R4b marked fixed** by #938 (`UpdateTags` update-only; `ParseAsync` drops its result when the
  buffer is gone). R4 is now only R4a.
- **R2 narrowed** to the window left after #938: `UpdateTags` succeeds, then the close sequence runs,
  then `Store`.
- **R3 reworded**: the race is per file, because each file's scan reads `provider.Current` when it
  reaches that file. Three triggers can start overlapping scans, including the `reqnroll.json`
  reconcile added after the first draft.
- **R5 added**: rename invalidates closed feature files without bumping or rescanning them. Adds a
  bump site, an I1 entry and a rescan.
- **R6 added**: project configuration (feature language) is a derivation input. The stamp gains a
  per-project `ConfigGeneration` and invariant I4.
- §1.2 writer table updated (rename invalidation, config reconcile through the patch path, no
  separate feature-rescan debouncer caller of `RescanClosedFileAsync`).
- Alternatives: added "fix each path locally" and why it isn't enough.
- During Phase 1: membership removal dropped as a content-bump site (a per-document bump for a
  per-project change breaks I1; the stale entry is now Q7), and the config handler's
  `.editorconfig` path confirmed as needing no config bump.
