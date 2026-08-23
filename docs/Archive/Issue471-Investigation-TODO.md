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
- [ ] 2. Add thread-ID + size/count tags to the hot PERF lines
      (`OperationDurationRecorder.cs:50`, `Measure`/`Record`):
      - `BindingMatchService.FindUsages` / `StepCodeLensHandler`: cache doc count, total
        step count at call time.
      - `BindingRegistryChangedHandler` reconcile: feature-file count, step count.
      Keep it cheap (Verbose-gated like today) — this is a diagnostics-only change.
- [ ] 3. Add a benchmark scenario to
      `tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks*` that grows the
      match-set cache to a few thousand steps and calls `StepCodeLensHandler`/
      `FindUsages` directly — fast, CI-runnable duration-vs-cache-size curve to confirm
      or kill hypothesis #1 quantitatively, without needing a full VS session.
- [ ] 4. Re-run the manual VS repro (`Reqnroll.VeryLargeFeature`) with the new
      instrumentation from #2, capture `reqnroll-vs-server-debug-*.log`, and correlate
      climbing duration against cache/registry size growth.
- [ ] 5. Cross-check VS-side request volume via protocol-log-level trace: confirm what
      VS actually re-requests per refresh broadcast (all open tabs vs. visible one) —
      currently unverified from the client side.
- [ ] 6. Write up findings as a comment on issue #471 (root cause + evidence), then scope
      the actual fix as follow-up issue(s)/PR(s) — do not fix inline as part of this
      investigation unless trivial and clearly in-scope.

## Notes / decisions log

- 2026-08-23: Branched from `origin/master` (not off the unrelated
  `experiment/remove-vsstubframeinitializer-timing` branch already checked out).
