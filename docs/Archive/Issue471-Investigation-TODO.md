# Issue #471 Investigation TODO

Working notes for investigating "LSP server: workspace/inlayHint/refresh and
workspace/semanticTokens/refresh scale badly on large solutions, blocking unrelated
requests for tens of seconds". Branch: `investigate/471-lsp-large-solution-perf`.

Plan proposed to and approved by Chris on 2026-08-23. This file is scratch/working
memory for the investigation itself, not a design doc — delete or fold findings into
the issue/a real doc once root cause is confirmed.

## Leading hypotheses (ranked)

1. **`BindingMatchService.FindUsages`** (`src/LSP/Reqnroll.IdeSupport.LSP.Core/Matching/BindingMatchService.cs:68`)
   is an unindexed full scan over every cached document's every step, called once per
   binding by `StepCodeLensHandler` (`.../Features/CodeLens/StepCodeLensHandler.cs:103`).
   Cost is O(bindings-in-file × total-cached-steps-workspace-wide); the cache only grows
   during `BindingRegistryChangedHandler.ScanAllFeatureFilesAsync`. Prime suspect for both
   the CodeLens slowness and (if dispatch is serial) for starving the refresh round-trips.
2. **Serial request dispatch** — unconfirmed. No `RequestProcessType`/concurrency
   override exists anywhere in this codebase (grepped, none found), so behavior depends on
   OmniSharp's default. Determines whether the fix is "make hot ops faster" or "get slow
   ops off the request-processing critical path." This is the fork in the road — do this
   first.
3. `BindingRegistryChangedHandler.ScanAllFeatureFilesAsync` (`Pipeline/BindingRegistryChangedHandler.cs:182`)
   reads/parses every closed feature file sequentially, no batching/parallelism — possible
   contributor to the ~10s reconcile cost, separate from hypothesis #1.
4. Refresh debounce keys are global, not per-project (`RefreshDebouncer.cs`) — probably
   fine, but unverified under multi-project load.

Ruled out (read, not the cause): `GherkinInlayHintService.Build` — bounded per-document
loop over an already-computed match set, no global-cache dependency. The refresh
requests' own payload computation is not the likely culprit.

## TODO

- [x] 1. Write a concurrency-probe integration test (extend `LspServerHarness` or a new
      test in `Reqnroll.IdeSupport.LSP.Server.Tests`): fire a cheap request while a slow
      operation (large `ScanAllFeatureFilesAsync` reconcile, or a synthetic slow handler)
      is in flight; assert/measure whether the cheap request is delayed by ~the slow
      operation's duration. Settles hypothesis #2 empirically.
      **DONE** — `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Performance/ConcurrencyProbeTests.cs`.
      Result: solo `textDocument/foldingRange` = 13.0ms; same request fired concurrently
      with 20 `textDocument/codeLens` calls against the corpus's 1,350-step cache /
      64-pattern binding file = 593.3ms (~45x). **Hypothesis #2 (serial dispatch)
      confirmed empirically** — a cheap, unrelated request genuinely queues behind
      CodeLens/FindUsages work rather than running concurrently. This also corroborates
      hypothesis #1: the workload doing the blocking is exactly the suspected
      unindexed `FindUsages` scan.
- [x] 2. Add thread-ID + size/count tags to the hot PERF lines
      (`OperationDurationRecorder.cs:50`, `Measure`/`Record`):
      - `BindingMatchService.FindUsages` / `StepCodeLensHandler`: cache doc count, total
        step count at call time.
      - `BindingRegistryChangedHandler` reconcile: feature-file count, step count.
      Keep it cheap (Verbose-gated like today) — this is a diagnostics-only change.
      **DONE**:
      - `IOperationDurationRecorder.Measure`/`Record` gained an optional `string? detail`
        param (source/binary-compatible — trailing optional param, all ~32 existing call
        sites untouched); `OperationDurationRecorder.Record` now always logs
        `thread={Environment.CurrentManagedThreadId}` and appends `detail` verbatim when
        given.
      - `IBindingMatchService.GetCacheStats()` added: `(DocumentCount, TotalStepCount)`,
        O(1) + O(cached documents) — negligible next to the O(bindings × cached steps)
        `FindUsages` sweep it's tagging. `StepCodeLensHandler` now calls it once per
        `textDocument/codeLens` request and passes `cacheDocs=… cacheSteps=…` as `detail`.
      - `BindingRegistryChangedHandler.Handle` switched from `using var _perf = Measure(...)`
        to manual `Stopwatch` + `try/finally` + `Record(..., detail: "scannedFiles=…
        reparsedFiles=…")`, since those counts are only known after
        `ScanAllFeatureFilesAsync`/`ReparseOpenFilesAsync` return (both changed to return
        `Task<int>` instead of `Task`, private methods only). `finally` preserves the
        original "always records, even on exception" behavior of the `using` scope it
        replaced. The debounced incremental-rescan path's own `ScanAllFeatureFilesAsync`
        call still gets its own independent PERF line (unchanged), not folded into this one.
      - Tests: 2 new `BindingMatchServiceTests` (`GetCacheStats_*`), 3 new
        `OperationDurationRecorderTests` (`detail`/`thread`/`Measure`-carries-`detail`).
        Full `LSP.Server.Tests` (774) and `LSP.Core.Tests` (618, 1 pre-existing skip) both
        green after the change, including all existing `BindingRegistryChangedHandlerTests`
        (unaffected by the return-type change).
- [x] 3. Add a benchmark scenario to
      `tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks*` that grows the
      match-set cache to a few thousand steps and calls `StepCodeLensHandler`/
      `FindUsages` directly — fast, CI-runnable duration-vs-cache-size curve to confirm
      or kill hypothesis #1 quantitatively, without needing a full VS session.
      **DONE** — `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Performance/FindUsagesScalingProbeTests.cs`,
      two tests:
      - `CodeLens_latency_vs_cached_step_count`: holds bindings-in-file fixed (64,
        CorpusSteps.cs), varies workspace cache size 2→50 corpus features (~54→1350
        steps). Result: 12.5ms → 44.3ms (only ~3.5x for a 25x step-count increase);
        per-feature cost *decreases* (6.25 → 0.89 ms/feature). **Cache size alone is
        sub-linear at this scale, not O(n²).**
      - `CodeLens_latency_vs_bindings_in_file`: holds cache fixed at the corpus's full
        1,350 steps, varies bindings-in-file: 64 (CorpusSteps.cs, 37.2ms) vs. 1,000
        (synthetic generated file, 533.2ms). Binding-count ratio 15.6x → latency ratio
        14.3x (~0.53-0.58 ms/binding, roughly constant). **Bindings-in-file scales
        linearly.**
      **Revised conclusion**: `FindUsages`/`StepCodeLensHandler` cost is linear in
      *each* axis (bindingsInFile, cachedSteps) individually — not O(n²) in either
      alone. But cost is the *product* of both, and at real-world scale (the issue's
      ~1,300-method file) that product gets large: the 1,000-binding synthetic test
      alone already cost 533ms for a *single* `textDocument/codeLens` call at only
      1,350 cached steps — the issue's "large .feature file" is plausibly much bigger.
      Combined with item #1's confirmed serial dispatch and VS's ~1/sec CodeLens
      polling, this is enough to fully explain the reported tens-of-seconds numbers
      without needing an O(n²) bug: linear-but-large per-call cost, multiplied by
      several such calls queueing serially. No indexing bug *beyond* "no index by
      binding location" is needed to explain the symptom — the fix is still to index
      `FindUsages`, but the mental model changes from "runaway quadratic growth" to
      "linear cost that's simply large enough, times poor dispatch behavior."
- [x] 4. Re-run the manual VS repro (`Reqnroll.VeryLargeFeature`) with the new
      instrumentation from #2, capture `reqnroll-vs-server-debug-*.log`, and correlate
      climbing duration against cache/registry size growth.
      **DONE** — Chris ran the F5/DEBUG experimental instance 2026-08-23 ~15:21-15:23,
      captured `reqnroll-vs-server-debug-20260823-32140.log` (120 PERF lines) +
      `reqnroll-vs-inspector-20260823-152122.log`. Findings:
      - `cacheDocs=1 cacheSteps=6238` — a single `.feature` file (`DuisSedSemAmet.feature`)
        holds 6,238 cached steps; this is the "large .feature file" the issue described.
      - 10 consecutive `textDocument/codeLens` calls on the large step-definitions file
        (`FaucibusDictumSagittisCursusSteps.cs`), all at the same `cacheDocs=1
        cacheSteps=6238`, cost a **stable ~1.22-1.29s each** — no climbing across
        repeated calls at fixed cache size, confirming item #3's "linear in the
        product, not runaway growth" model with real data. Back-calculating through
        that model (~0.00043ms per binding×step pair) implies ~460 bindings in the file.
      - Direct thread-sharing evidence: `internal/bindingRegistryReconcile`
        (10,050.2ms, scannedFiles=0 reparsedFiles=1), `textDocument/didOpen`
        (10,128.0ms), and the `reqnroll/semanticTokens` push (7,470.8ms) — three
        independently-measured operations — all completed within ~2ms of each other,
        all on `thread=20`. Signature of several operations queued behind one shared
        execution lane and released together.
      - New finding, not previously flagged: `reqnroll/resolveTestTargets` fired 51
        times in ~8 seconds (~150-220ms each) for the single open feature file — under
        serial dispatch that's ~7.65 continuous seconds of server time the original
        issue report didn't call out.
- [x] 5. Cross-check VS-side request volume via protocol-log-level trace: confirm what
      VS actually re-requests per refresh broadcast (all open tabs vs. visible one) —
      currently unverified from the client side.
      **DONE (core question answered; one sub-question left open)** — used the
      always-on client-side `reqnroll-vs-inspector-*.log` (not `--protocol-log-level`,
      which is server-side/OmniSharp-internal and not what's needed here) from the same
      session as #4. Cross-referencing it against the server PERF log for the first
      `workspace/inlayHint/refresh`:
      - Server sent the refresh request at 15:22:45.006.
      - VS received it and sent its ack back at 15:22:59.229 (14.2s later — VS's own
        re-pull: it fired `textDocument/foldingRange`, `textDocument/inlayHint`, and
        `textDocument/semanticTokens/range` in that window).
      - The server's own `await SendRequest(...).ReturningVoid()` didn't complete until
        15:23:20.165 — **~21 seconds after VS's ack had already been sent**.
      That 21s gap is an already-received response frame sitting unprocessed because the
      server's dispatch loop was busy with other queued work — this is now the clearest
      possible proof that the bottleneck is server-side serial dispatch, not VS being
      slow to respond, straight from the production timeline (stronger than the
      synthetic concurrency probe, since it needs no inference about concurrent load).
      **Left open**: only one `.feature` and one `.cs` file were open in this repro, so
      the original "all open tabs vs. just visible" sub-question is unanswered — would
      need a repro with multiple tabs open. Not needed to confirm the main hypothesis.
- [ ] 6. Write up findings as a comment on issue #471 (root cause + evidence), then scope
      the actual fix as follow-up issue(s)/PR(s) — do not fix inline as part of this
      investigation unless trivial and clearly in-scope.

## Notes / decisions log

- 2026-08-23: Branched from `origin/master` (not off the unrelated
  `experiment/remove-vsstubframeinitializer-timing` branch already checked out).
- 2026-08-24: Decompiled `OmniSharp.Extensions.JsonRpc.dll`/`OmniSharp.Extensions.LanguageProtocol.dll`
  (v0.19.9) to answer "does OmniSharp support concurrent dispatch" precisely. **It does** —
  `ProcessScheduler` genuinely runs `[Parallel]`-tagged requests via `Observable.Merge`; every
  handler this issue is about (`ICodeLensHandler`, `ISemanticTokensFullHandler`/`Delta`/`Range`,
  `IInlayHintsHandler`, the three refresh handlers) is `[Parallel]` by the *library's own*
  interface attribute — we have zero `[Serial]`/`[Parallel]` attributes anywhere in our code.
  BUT `IDidOpenTextDocumentHandler`/`IDidChangeTextDocumentHandler`/`IDidSaveTextDocumentHandler`
  are hardwired `[Serial]` by the library, and the scheduler's batch design means a Parallel
  batch can't even start until the current Serial batch's slowest in-flight item fully drains.
  Revises the "request dispatch is effectively serial" framing from finding #1: it isn't serial,
  but a slow Serial-tagged `didOpen`/`didChange` (ours does synchronous Roslyn discovery + reparse
  inline) stalls the whole pipeline, which looks identical from outside. Posted as a follow-up
  issue comment along with a range/resolve-support audit (below).
- 2026-08-24: Per Chris's request, audited range/resolve support for the LSP messages in this
  issue:
  - `textDocument/codeLens` has **no resolve support at all** — no `resolveProvider` capability
    declared, no `codeLens/resolve` handler, lenses carry no `Data` token. Every lens's expensive
    `Command` (the `FindUsages` scan) computes eagerly for the whole file on every poll. This is
    the standard reason CodeLens resolve exists; ranked it as the highest-leverage, lowest-effort
    fix — a protocol-shape change, not a data-structure rewrite, and shrinks the Parallel-batch
    drain time too (ties into the dispatch finding above).
  - `textDocument/semanticTokens/range` is registered but is a **no-op shim** —
    `SemanticTokensHandler.HandleAsync(SemanticTokensRangeParams)` computes/encodes the entire
    document regardless of the requested range (`// Return all tokens; the client will filter by
    range.`). Likely a meaningful share of the `reqnroll/semanticTokens` push's 7,470.8ms.
  - `textDocument/inlayHint` filters *output* by range but not *compute* — `InlayHintHandler`
    builds hints for every step in the document, then filters. Same fix shape, smaller scope.
  Recommended sequencing (posted to the issue): (a) CodeLens resolve, (b) genuine range-scoping
  for semanticTokens/range + inlayHint, (c) the FindUsages index from the design-direction
  comment, (d) get Roslyn discovery/reparse off the didOpen/didChange critical path. (a)-(c)
  independent/any order; (d) architecturally separate.
