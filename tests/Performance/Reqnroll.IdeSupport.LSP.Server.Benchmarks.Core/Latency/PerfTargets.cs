#nullable enable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

/// <summary>Whether a performance target is an interactive round-trip (P95) or a batch op (wall-clock).</summary>
public enum PerfTargetKind
{
    /// <summary>Interactive: assert the P95 percentile against the threshold.</summary>
    InteractiveP95,

    /// <summary>Batch/throughput: assert the median (or max) wall-clock against the threshold.</summary>
    Batch,
}

/// <summary>
/// One latency target from the architecture's Performance Requirements. <see cref="Operation"/> matches the operation
/// label used by the benchmark scenarios and the Layer 4 recorder, so field and synthetic numbers
/// line up.
/// </summary>
/// <param name="IncludesFixedDelay">
/// True when the measured number is not pure processing cost but includes a fixed
/// debounce/coalescing window the pipeline deliberately waits out before reacting (e.g. the
/// 500ms debounce in <c>SemanticTokensRefreshHandler</c>/<c>InlayHintRefreshHandler</c>/
/// <c>CodeLensRefreshHandler</c>/<c>FeatureRescanDebouncer</c>). Surfaced as a footnote in the
/// console report so the number isn't mistaken for slow computation.
/// </param>
public sealed record PerfTarget(
    string Operation, double TargetMs, PerfTargetKind Kind, string Description, bool IncludesFixedDelay = false);

/// <summary>
/// The architecture's Performance Requirements table, as code (see LSP-IDE-Support-Architecture.md).
/// The single source of truth for what the benchmark asserts on the reference machine.
/// </summary>
public static class PerfTargets
{
    public static readonly PerfTarget SemanticTokensFull =
        new("textDocument/semanticTokens/full", 100, PerfTargetKind.InteractiveP95, "Semantic tokens from last didChange");

    public static readonly PerfTarget CompletionKeyword =
        new("textDocument/completion#keyword", 50, PerfTargetKind.InteractiveP95, "Keyword completion (F7)");

    public static readonly PerfTarget CompletionStep =
        new("textDocument/completion#step", 150, PerfTargetKind.InteractiveP95, "Step completion (F8)");

    public static readonly PerfTarget DefinitionCacheHit =
        new("textDocument/definition", 100, PerfTargetKind.InteractiveP95, "Go to definition, cache hit (F5)");

    public static readonly PerfTarget PublishDiagnostics =
        new("textDocument/publishDiagnostics", 500, PerfTargetKind.InteractiveP95, "Diagnostics push from end of debounce",
            IncludesFixedDelay: true);

    public static readonly PerfTarget RoslynReDiscovery =
        new("discovery/roslyn-single-cs", 2000, PerfTargetKind.Batch, "Roslyn binding re-discovery, single .cs file");

    public static readonly PerfTarget ReflectionDiscovery =
        new("discovery/reflection-post-build", 10000, PerfTargetKind.Batch, "Reflection binding discovery, post-build");

    public static readonly PerfTarget ColdStartScan =
        new("workspace/cold-start-scan", 30000, PerfTargetKind.Batch, "Initial workspace scan, cold start");

    // Load-only operations: they participate in the editing-session burst to create realistic
    // contention, but the Performance Requirements table publishes no threshold for them, so
    // TargetMs is 0 (rendered "—", never asserted). Not part of <see cref="All"/>.
    public static readonly PerfTarget DocumentSymbol =
        new("textDocument/documentSymbol", 0, PerfTargetKind.InteractiveP95, "Document outline (session load)");

    public static readonly PerfTarget FoldingRange =
        new("textDocument/foldingRange", 0, PerfTargetKind.InteractiveP95, "Folding ranges (session load)");

    // Measured-but-unpublished operations (issue #119): field-instrumented (see #113/#118) but no
    // threshold exists yet in the architecture's §9 Performance Requirements table, so TargetMs is
    // 0 (rendered "—", never asserted) until one is proposed and published there. Not part of
    // <see cref="All"/>. Step Rename and Find Unused Step Definitions are workspace-wide operations
    // (Batch-classified — median wall-clock, like the discovery scenarios) rather than a per-request
    // percentile.
    public static readonly PerfTarget StepPrepareRename =
        new("textDocument/prepareRename", 0, PerfTargetKind.InteractiveP95, "Prepare rename validity check (F16)");

    public static readonly PerfTarget RenameTargets =
        new("reqnroll/renameTargets", 0, PerfTargetKind.InteractiveP95, "Rename target picker candidates (F16)");

    public static readonly PerfTarget StepRename =
        new("textDocument/rename", 0, PerfTargetKind.Batch, "Step rename, workspace-wide WorkspaceEdit (F16)");

    public static readonly PerfTarget FindUnusedStepDefinitions =
        new("reqnroll/findUnusedStepDefinitions", 0, PerfTargetKind.Batch, "Find unused step definitions, workspace-wide scan (F15)");

    public static readonly PerfTarget SemanticTokensDelta =
        new("textDocument/semanticTokens/full/delta", 0, PerfTargetKind.InteractiveP95, "Semantic tokens delta pull");

    // Remaining previously-dark handlers from issue #113/#118, synthetic coverage added by #119
    // follow-up. Same load-only rationale as above: field-instrumented, no published threshold.
    public static readonly PerfTarget FindStepUsages =
        new("reqnroll/findStepUsages", 0, PerfTargetKind.InteractiveP95, "Find step usages from a .cs binding (F17)");

    public static readonly PerfTarget StepReferences =
        new("textDocument/references", 0, PerfTargetKind.InteractiveP95, "Find references from a .feature step");

    public static readonly PerfTarget GoToHooks =
        new("reqnroll/goToHooks", 0, PerfTargetKind.InteractiveP95, "Go to hook bindings for a step/scenario");

    public static readonly PerfTarget StepCodeLens =
        new("textDocument/codeLens", 0, PerfTargetKind.InteractiveP95, "Step code lens on a .cs binding file (F18)");

    // Hook-facing CodeLens/navigation (issues #269/#372/#373): the operation label carries a
    // "#..." suffix (same convention as the completion variants above) because all three share the
    // raw "textDocument/codeLens"/"reqnroll/goToHooks" wire method with an existing target above —
    // without it they'd collide in the report and overwrite each other.
    public static readonly PerfTarget FeatureHookCodeLens =
        new("textDocument/codeLens#feature-hooks", 0, PerfTargetKind.InteractiveP95,
            "Hook-match-count code lens on a .feature file (F18/#269/#372)");

    public static readonly PerfTarget HookMatchCountCodeLens =
        new("textDocument/codeLens#hook-match-count", 0, PerfTargetKind.InteractiveP95,
            "Scenario-match-count code lens on a .cs hook binding (F18/#373)");

    public static readonly PerfTarget GoToMatchingScenarios =
        new("reqnroll/goToMatchingScenarios", 0, PerfTargetKind.InteractiveP95,
            "Go to matching scenarios from a .cs hook binding (#373)");

    // Issue #495: the one reqnroll/* operation the Run CodeLens bridge depends on had neither a
    // PerfTargets entry nor an InteractiveScenarios method, despite being field-instrumented via
    // IOperationDurationRecorder in ResolveTestTargetsHandler since #262/#492 — the gap the whole-file
    // walk investigation (#491-#495) exposed but never closed. Single-request latency only; the
    // WorkspaceReloadContentionScenario-shaped concurrent-callers case lives alongside
    // ResolveTestTargetsContentionScenario.
    public static readonly PerfTarget ResolveTestTargets =
        new("reqnroll/resolveTestTargets", 0, PerfTargetKind.InteractiveP95,
            "Resolve generated test target(s) for a scenario (Run CodeLens, #262/#495)");

    public static readonly PerfTarget DocumentFormatting =
        new("textDocument/formatting", 0, PerfTargetKind.InteractiveP95, "Whole-document Gherkin formatting (F11)");

    public static readonly PerfTarget RangeFormatting =
        new("textDocument/rangeFormatting", 0, PerfTargetKind.InteractiveP95, "Range Gherkin formatting (F11)");

    public static readonly PerfTarget OnTypeFormatting =
        new("textDocument/onTypeFormatting", 0, PerfTargetKind.InteractiveP95, "On-type table formatting (F12)");

    public static readonly PerfTarget InlayHint =
        new("textDocument/inlayHint", 0, PerfTargetKind.InteractiveP95, "Binding-info inlay hints (F23)");

    public static readonly PerfTarget CodeAction =
        new("textDocument/codeAction", 0, PerfTargetKind.InteractiveP95, "Undefined-step quick-fix scaffold (F6)");

    // Indirect/reaction scenarios: not a single request/response — the client sends a trigger
    // (a watched-files change, an edit) and the benchmark waits for the resulting server-initiated
    // push/request, so the measured number includes the pipeline's fixed debounce window (same
    // "from last didChange"-style honesty as PublishDiagnostics). Batch-classified: coarse,
    // workspace-reaction wall-clock, not a per-request percentile.
    public static readonly PerfTarget WatchedFilesReconfig =
        new("workspace/didChangeWatchedFiles#reqnroll.json", 0, PerfTargetKind.Batch,
            "reqnroll.json change reaction (WatchedFilesHandler -> ReqnrollConfigChangedHandler -> re-diagnose)",
            IncludesFixedDelay: true);

    public static readonly PerfTarget SemanticTokensRefresh =
        new("workspace/semanticTokens/refresh", 0, PerfTargetKind.Batch, "Server-initiated semantic tokens refresh push",
            IncludesFixedDelay: true);

    public static readonly PerfTarget InlayHintRefresh =
        new("workspace/inlayHint/refresh", 0, PerfTargetKind.Batch, "Server-initiated inlay hint refresh push",
            IncludesFixedDelay: true);

    // Issue #256: the sibling refresh pushes above were both benchmarked, but code lens's own push
    // (CodeLensRefreshHandler / workspace/codeLens/refresh) had no coverage — an inconsistency, not
    // a deliberate omission, since it follows the exact same debounced-push shape.
    public static readonly PerfTarget CodeLensRefresh =
        new("workspace/codeLens/refresh", 0, PerfTargetKind.Batch, "Server-initiated code lens refresh push",
            IncludesFixedDelay: true);

    // Issues #514/#516: live structural-validation diagnostics on .cs binding methods (non-static,
    // async void, malformed step/tag expression) had no benchmark coverage -- the sibling
    // PublishDiagnostics target above only ever exercises a .feature document's diagnostics push.
    // Same "#..." wire-method-collision-avoidance suffix convention as the completion/codeLens
    // variants above.
    public static readonly PerfTarget CSharpDiagnosticsPush =
        new("textDocument/publishDiagnostics#cs-binding", 0, PerfTargetKind.InteractiveP95,
            "Diagnostics push from a .cs binding structural-validation edit (#514/#516)");

    // Issue #531: CSharpBindingDiscoveryService.UpdateFromSourceAsync's staleness short-circuit for
    // rapid .cs typing (mirrors the pre-existing Gherkin-side one) had no regression coverage --
    // SessionScenario's supersede/cancel burst only ever edits .feature documents. Batch-classified
    // like its sibling discovery scenarios: coarse wall-clock, not a per-request percentile.
    public static readonly PerfTarget CSharpRapidEditBurst =
        new("discovery/roslyn-rapid-cs-burst", 0, PerfTargetKind.Batch,
            "Roslyn re-discovery after a rapid burst of superseded .cs edits (staleness short-circuit, #531)");

    /// <summary>All performance targets, in table order.</summary>
    public static readonly IReadOnlyList<PerfTarget> All = new[]
    {
        SemanticTokensFull, CompletionKeyword, CompletionStep, DefinitionCacheHit, PublishDiagnostics,
        RoslynReDiscovery, ReflectionDiscovery, ColdStartScan,
    };
}
