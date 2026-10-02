#nullable enable

using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

/// <summary>Knobs for <see cref="RenameSerialLaneContentionScenario"/>.</summary>
/// <param name="CeilingRatio">
/// Unlike <c>WorkspaceReloadContentionScenario</c>'s 30x and <c>ResolveTestTargetsContentionScenario</c>'s
/// 200x, this default is <b>not</b> backed by a local measurement — this scenario has not yet been
/// run against a built corpus in this environment. It is a starting placeholder only, deliberately
/// generous: because rename now shares the same Serial FIFO the storm occupies (R8) rather than
/// racing it from a separate Parallel path, the relationship here is queuing delay, not scheduler
/// unfairness, and could plausibly need a materially different ceiling than either precedent once
/// real reference-machine data exists. Recalibrate from an actual run before trusting this as a
/// regression gate.
/// </param>
public sealed record RenameSerialLaneContentionOptions(
    int Repetitions = 5,
    double CeilingRatio = 50.0,
    int SettleDelayMs = 300);

/// <summary>
/// Dispatch-fairness scenario for <c>textDocument/rename</c>'s Serial-lane dispatch (issue #671,
/// R8): does a rename's own latency stay reasonable while the user keeps typing in other open
/// files?
/// </summary>
/// <remarks>
/// <para>
/// Rename used to run on OmniSharp's default Parallel lane, racing (and, per issue #654, sometimes
/// losing to) whatever Serial-dispatched <c>didChange</c>/<c>didOpen</c>/<c>didSave</c> traffic was
/// in flight. R8 moved it onto the Serial lane instead, for the multi-document snapshot consistency
/// a rename spanning several files needs — but that trades one failure mode for a different cost:
/// rename now shares one shared, global FIFO queue with every routine edit notification, in any
/// open file, not just the one being renamed. This scenario is the coverage that trade explicitly
/// needs: does a typing storm elsewhere in the workspace make a concurrent rename noticeably
/// slower?
/// </para>
/// <para>
/// Follows <see cref="WorkspaceReloadContentionScenario"/>'s baseline/under-load/ceiling-ratio shape
/// (issue #488) rather than inventing a new one: the "many tabs react to one event" storm there is
/// exactly the "many keystrokes fire didChange" storm here, just measured against a rename instead
/// of a cheap unrelated read.
/// </para>
/// <para>
/// Never confirms the rename with <c>reqnroll/renameApplied</c> — matching
/// <c>BatchScenarios.StepRenameAsync</c>'s own invariant, for the same reason: confirming would
/// commit a real registry mutation against the shared corpus, corrupting every scenario that runs
/// afterward in the same server session. This scenario measures dispatch fairness, not the
/// confirm-and-commit cost <c>BatchScenarios.RenameApplyCommitAsync</c> covers separately in its
/// own throwaway, self-contained binding.
/// </para>
/// </remarks>
public sealed class RenameSerialLaneContentionScenario
{
    public const string Operation = "textDocument/rename#serial-lane-under-didChange-storm";

    private readonly BenchmarkLspHarness _harness;
    private readonly IReadOnlyList<OpenFeature> _typingFiles;
    private readonly OpenFeature _renameTarget;
    private readonly RenameSerialLaneContentionOptions _options;

    /// <param name="typingFiles">
    /// The "someone keeps typing" storm targets. Must already be open on <paramref name="harness"/>,
    /// and must not be <paramref name="renameTarget"/> — the storm must never touch the file the
    /// rename itself reads, or the two would be measuring the same edit instead of independent
    /// Serial-lane traffic.
    /// </param>
    /// <param name="renameTarget">
    /// An already-open feature file whose bound step is renamed every repetition. Any corpus
    /// feature using the shared bound step works — the rename is never confirmed, so (like
    /// <c>StepRenameAsync</c>) repetitions are idempotent and no state carries between them.
    /// </param>
    public RenameSerialLaneContentionScenario(
        BenchmarkLspHarness harness, IReadOnlyList<OpenFeature> typingFiles, OpenFeature renameTarget,
        RenameSerialLaneContentionOptions options)
    {
        _harness = harness;
        _typingFiles = typingFiles;
        _renameTarget = renameTarget;
        _options = options;
    }

    public async Task<ContentionCheck> RunAsync()
    {
        var baseline = new LatencyRecorder(Operation + "-baseline");
        var underLoad = new LatencyRecorder(Operation);
        var version = 1000;
        var (line, character) = _renameTarget.StepPosition;

        // Appended after the full original expression, never inserted before or within it:
        // NewNameReconciler diffs the edited text against the original to separate wording
        // changes from parameter-value changes, and a word inserted before the bound parameter
        // (e.g. "precondition renamed 0 is met") confuses that diff into rejecting the edit as a
        // parameter-value change rather than a wording change — see FirstStepExpression's remarks.
        var original = _renameTarget.FirstStepExpression;

        for (var rep = 0; rep < _options.Repetitions; rep++)
        {
            // Solo baseline: rename with no concurrent Serial-lane traffic.
            var baselineStart = Stopwatch.GetTimestamp();
            await _harness.RequestRenameAsync(_renameTarget.Uri, line, character,
                $"{original} (baseline {rep})").ConfigureAwait(false);
            baseline.Add(Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds);

            // The typing storm: didChange fired across every OTHER open file, back to back, the
            // same "issued together" shape a real client's keystrokes produce on one connection —
            // not a sequence of separately-awaited edits. Since R8 put textDocument/rename on the
            // same Serial dispatch lane these notifications occupy, the rename below now queues
            // FIFO alongside them instead of racing past on a separate Parallel path.
            version++;
            foreach (var f in _typingFiles)
                _harness.ChangeFeature(f.Uri, version, f.Text + $"\n  # serial-lane-storm rep {rep} v{version}\n");

            // Measure only the rename's own round-trip while the storm is in flight -- do not fold
            // the storm's own settle time into this number.
            var probeStart = Stopwatch.GetTimestamp();
            await _harness.RequestRenameAsync(_renameTarget.Uri, line, character,
                $"{original} (under-load {rep})").ConfigureAwait(false);
            underLoad.Add(Stopwatch.GetElapsedTime(probeStart).TotalMilliseconds);

            // Let this repetition's storm (reparse/diagnostics/debounced refreshes) settle before
            // the next repetition's baseline sample, so a straggler doesn't bleed into it.
            if (_options.SettleDelayMs > 0)
                await Task.Delay(_options.SettleDelayMs).ConfigureAwait(false);
        }

        return new ContentionCheck(Operation, baseline.Summarize(), underLoad.Summarize(), _options.CeilingRatio);
    }
}
