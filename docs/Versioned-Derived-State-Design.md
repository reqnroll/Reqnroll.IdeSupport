# Versioned Derived State — Design

**Status: PROPOSED (2026-10-05).** Tracking issue: #906. Migration plan:
[Versioned-Derived-State-Migration-Plan.md](Versioned-Derived-State-Migration-Plan.md).

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
#469 / #515 (duplicate bindings).

---

## 1. Problem

### 1.1 The stamps exist but are never read

| Artifact | Version information today | Enforced? |
|---|---|---|
| `DocumentBuffer` (`LSP.Server/Documents`) | `int? Version` — client's document version | No: `Update` and `UpdateTags` overwrite unconditionally |
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
| `GherkinDocumentTaggerService.ParseAsync` via `FeatureDocumentReparser` | `didOpen` / `didChange` (`TextDocumentSyncHandler`), `DocumentActivatedHandler`, `ReqnrollConfigChangedHandler`, `BindingRegistryChangedHandler` (open files) | **Yes** — per-URI chain |
| `GherkinDocumentTaggerService.ScanClosedFileAsync` | `BindingRegistryChangedHandler` full-replacement scan of closed files | **No** |
| `GherkinDocumentTaggerService.RescanClosedFileAsync` | `didClose` (inline on the Serial lane), feature-rescan debouncer | **No** |
| `BindingMatchService.InvalidateAllForDocument` / `InvalidateAllForProject` | `didClose`, project removal (`BindingRegistryProviderRouter.OnProjectRemoved`) | **No** |

The registry-change events themselves are published with `FireAndForget` from
`BindingRegistryProviderRouter.OnProviderChanged` (#477). Two registry changes for the same project in
quick succession therefore run `BindingRegistryChangedHandler` **concurrently**, with nothing ordering
them.

### 1.3 Concrete stale-write paths

All four were found by reading the code. **None has been reproduced yet**; Phase 0 of the migration
plan writes deterministic tests for each.

- **R1 — Closed-file scan overwrites an open file.** `ScanClosedFileAsync` checks
  `_documentBufferService.TryGet(uri)` and skips the scan if the file is open. That's a check-then-act:
  a `didOpen` can land between the check and `Store`. The closed scan then replaces the open file's
  match set with one built from disk text (`documentVersion: null`), possibly against an older
  registry. The method's own comment says the guard only narrows the race.
- **R2 — A parse started before `didClose` lands after it.** The `didClose` handler removes the
  buffer, calls `InvalidateAllForDocument`, then rescans from disk. A `ParseAsync` scheduled earlier
  for the open buffer runs off the Serial lane. If it reaches `Store` after the invalidation, it puts
  back a match set built from the editor text, which may have been discarded. If it lands after the
  rescan, it replaces the disk-based entry.
- **R3 — Two registry changes race on closed files.** Registry vN and vN+1 for project P each trigger
  a full closed-file scan (R1/R2 do not apply — files are closed). Both loops run concurrently, so for
  any file the vN result can land after the vN+1 result. The cache then holds matches against the old
  bindings until the next registry change.
- **R4 — Tags from an older buffer get attached to a newer one, or bring a closed buffer back.**
  - `didChange` calls `DocumentBufferService.Update` inline. That replaces the buffer and clears
    `Tags`. The parse runs later on the coordinator chain. If parse N is still running when
    `Update(N+1)` lands, parse N's `UpdateTags` attaches N's tags to the N+1 buffer. They stay there
    until parse N+1 finishes.
  - Worse, `UpdateTags` uses `AddOrUpdate`. If the buffer was removed by `didClose` while the parse
    was running, `UpdateTags` **creates** a buffer with empty text and a null version. The file then
    looks open: `ScanClosedFileAsync`'s guard skips it on every later closed scan, and loops over
    `IDocumentBufferService.All` include it.

R1–R3 produce wrong usage counts, find-usages results and rename coverage. Those features read the
cache across documents, so a wrong entry stays until the next re-derivation of that file. R4's
resurrection case can make a file vanish from workspace-wide features until it is reopened.

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
| Registry version `int` | per `ProjectBindingRegistry` instance, unique across the process | `ProjectBindingRegistry.Version` | existing |
| Client document version `int?` | per open document | LSP client | existing — kept only for wire messages (`publishDiagnostics.version`, `WorkspaceEdit`) |

**Why a server-side content generation instead of the client version.** The client version doesn't
exist for closed files, and it resets when a file is reopened. The R1 and R2 races are exactly about
moving between open and closed. A per-URI counter owned by the server goes up on every event that
changes what the server believes the text is:

- `textDocument/didOpen`, `didChange`, `didClose` (a close switches the source of truth from the
  editor buffer to the file on disk);
- a feature file leaving the project (a membership delta via `reqnroll/projectFiles`).

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
public readonly record struct DerivationStamp(long ContentGeneration, int RegistryVersion)
    : IComparable<DerivationStamp>
{
    // Lexicographic: newer text always wins; for the same text, the newer registry wins.
    public int CompareTo(DerivationStamp other) => ...;
}
```

`ContentGeneration` is **read before the text is read**:

- for an open file, it's captured together with the buffer snapshot;
- for a closed file, it's captured before `ReadAllTextAsync`.

This prevents a stamp that is newer than the content it describes.

**Why lexicographic order and not "dominates on both".** With a strict "both components ≥" rule,
`(g6, r8)` and `(g5, r9)` are incomparable. Whatever tie-break you pick, the next incomparable write
can flip it back. A total order avoids that. Lexicographic order is correct as long as these hold:

- **I1 — every content bump is followed by a re-derivation of the new content.** `didOpen`,
  `didChange` → scheduled parse. `didClose` → `RescanClosedFileAsync`. Membership removal →
  invalidation. *Unconfirmed:* `MembershipIndex` currently publishes a purge
  (`ProjectBindingFilesRemovedNotification`) only for files in the **binding** role, and no
  feature-file removal path was found. Phase 1 confirms this, and if it's a gap adds the bump plus
  `InvalidateAllForDocument` together.
- **I2 — every registry change re-derives that project's entries.** Replaced → every file. Patched →
  open files immediately, closed files after the rescan debounce. (Existing behaviour, #577.)
- **I3 — registry reads are monotonic per project.** A derivation that starts later never sees an
  older registry than one that started earlier. (Holds today: `provider.Current` is one reference
  replaced by a newer instance.)

Given I3, a newer content generation in practice comes with a registry version that is equal or
higher, so the two orderings agree. If a write ever has a newer generation but a *lower* registry
version, the store logs a warning ("I3 violated") rather than silently accepting it.

### 3.3 Write rule

A guarded store (`BindingMatchService.Store`, `DocumentBufferService.UpdateTags`) accepts an incoming
entry `e` for key `k` only if **both** hold:

1. **Not superseded:** `e.Stamp.ContentGeneration == generations.Current(k.DocumentId)`. If the
   document has changed since the input was read, drop the write. This check covers R2 and the
   resurrection case of R4, where there's no stored entry to compare against.
2. **Not stale:** there's no stored entry, or `e.Stamp >= stored.Stamp`. This covers R1, R3 and the
   stale-tags case of R4.

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
| `DocumentBufferService.UpdateTags` | `bool UpdateTags(uri, generation, tags)`. Applies only if the buffer **exists** and `buffer.Generation == generation`. **Never creates a buffer.** Fixes R4. |
| `FeatureBindingMatchSet` | adds `DerivationStamp Stamp`. `DocumentVersion` stays (diagnostics need it on the wire). `RegistryVersion` becomes `Stamp.RegistryVersion`. |
| `BindingMatchService.Store` | the rule from §3.3. Returns `StoreOutcome`. Counts accepted and rejected writes in `GetCacheStats`. |
| `BindingMatchService.InvalidateAllForDocument` | no change. The generation bump that comes before it (on `didClose`, membership removal) is what rejects late writes. |
| `SemanticTokensService` cache | key becomes `(uri, Stamp)` instead of `(uri, clientVersion)`. The explicit `InvalidateCache` call in `ParseAsync` becomes unnecessary and is removed. `PurgePriorVersions` compares stamps. |
| `FeatureTagIndex` | compares `Generation` instead of the client `Version` (same logic, and it now works for closed files). |

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
| **Formal CQRS** (command/query markers, a dispatcher, one handler per operation) | Mostly renames what is already there. OmniSharp already dispatches requests and notifications through MediatR. It doesn't address any of R1–R4. |
| **Route every writer through `ParseCoordinator`** | Solves R1/R2/R4 for one URI but not R3: the race is between two *registry* events, not two edits of one file. It also puts bulk scans behind interactive edits. |
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
  practice.
- **Risk: a wrongly rejected write leaves a cached entry missing or stale.** This is the main risk:
  an I1 gap means rule 1 drops a write and nothing follows it up. That's why Phase 4 ships in
  **log-only mode** first. Counters and logs over real sessions show whether any rejection was
  unexpected before enforcement is switched on.
