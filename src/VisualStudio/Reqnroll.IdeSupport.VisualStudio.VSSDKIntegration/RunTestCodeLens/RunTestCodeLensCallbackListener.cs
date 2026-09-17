#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.TestLogger;
using StreamJsonRpc;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// In-process (<c>devenv.exe</c>) callback target for the out-of-process Run CodeLens data point
/// provider (design doc §5/§6, issue #262). Mirrors <c>HookCodeLensCallbackListener</c> exactly —
/// see its remarks for why <c>[ContentType]</c> is required (not decorative) and why a static
/// bridge alone can't reach across the OOP ServiceHub process boundary.
/// </summary>
[Export(typeof(ICodeLensCallbackListener))]
[ContentType("Gherkin")]
public sealed class RunTestCodeLensCallbackListener : ICodeLensCallbackListener
{
    /// <summary>Resolves exactly one line's Run target(s), instead of the whole file (issue #495).</summary>
    public const string GetTargetsForLineMethod = "Reqnroll.RunTestCodeLens.GetTargetsForLine";

    /// <summary>
    /// Last-known outcome of one generated test method from the in-proc <c>TestOutcomeStore</c> (fed by
    /// the bundled VSTest logger — implementation plan §4.2). Null when no run has reported it.
    /// </summary>
    public const string GetOutcomeMethod = "Reqnroll.RunTestCodeLens.GetOutcome";

    private readonly TestOutcomeStore _outcomeStore;

    [ImportingConstructor]
    public RunTestCodeLensCallbackListener(TestOutcomeStore outcomeStore)
    {
        _outcomeStore = outcomeStore ?? throw new ArgumentNullException(nameof(outcomeStore));
    }

    // Standalone file logger (no DI/MEF import needed) — same log file as the rest of the
    // extension's devenv.exe activity, since this class always runs in-process there (unlike its
    // OOP counterpart RunTestCodeLensDataPoint, which needs its own logger instance because it
    // runs under a different process/PID entirely). Added while investigating a live report of the
    // Run CodeLens rendering but never producing a working Details popup on click (no prior
    // instrumentation existed on either side of this OOP↔in-process callback boundary).
    private static readonly IIdeSupportLogger Logger = new SynchronousFileLogger("vs", "ext", TraceLevel.Verbose);

    [JsonRpcMethod(GetTargetsForLineMethod)]
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsForLineAsync(string fileUri, int line, CancellationToken cancellationToken)
    {
        Logger.LogVerbose($"RunTestCodeLensCallbackListener: GetTargetsForLineAsync called for {fileUri}:{line}");

        var fetch = RunTestCodeLensRedirect.GetTargetsForLineAsync;
        if (fetch is null)
        {
            Logger.LogWarning("RunTestCodeLensCallbackListener: RunTestCodeLensRedirect.GetTargetsForLineAsync is null — LSP connection not wired up yet; returning empty.");
            return Array.Empty<RunTestTargetEntry>();
        }

        try
        {
            var entries = await fetch(fileUri, line, cancellationToken).ConfigureAwait(false);
            Logger.LogVerbose($"RunTestCodeLensCallbackListener: GetTargetsForLineAsync returning {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} for {fileUri}:{line}");
            return entries;
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, $"RunTestCodeLensCallbackListener: GetTargetsForLineAsync threw for {fileUri}:{line}");
            throw;
        }
    }

    [JsonRpcMethod(GetOutcomeMethod)]
    public Task<RunTestOutcomeEntry?> GetOutcomeAsync(string assemblyPath, string typeFullName, string methodName, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = _outcomeStore.TryGet(assemblyPath, typeFullName, methodName);
            var aged = outcome is not null && !outcome.IsRunning && IsTooOldToTrust(outcome);
            if (aged)
            {
                // Unlike rebuild-staleness below, an aged-out entry isn't "known to be wrong" — it's
                // "we can no longer vouch for it" (see MaxTrustedAge's remarks). Treat it exactly like
                // the store never heard of this method at all, so the caller falls through to the live
                // bridge instead of the dead end a stale-but-still-returned entry would be.
                Logger.LogVerbose($"RunTestCodeLensCallbackListener: GetOutcomeAsync {typeFullName}.{methodName} → aged out ({DateTime.UtcNow - outcome!.LastUpdatedUtc} since last seen); falling back to the bridge");
                return Task.FromResult<RunTestOutcomeEntry?>(null);
            }

            var stale = outcome is not null && !outcome.IsRunning && IsStale(outcome);
            Logger.LogVerbose($"RunTestCodeLensCallbackListener: GetOutcomeAsync {typeFullName}.{methodName} → {outcome?.Aggregate.ToString() ?? "(none)"}{(outcome?.IsRunning == true ? " (running)" : string.Empty)}{(stale ? " (stale)" : string.Empty)}");
            return Task.FromResult(outcome is null ? null : ToEntry(outcome, stale));
        }
        catch (Exception ex)
        {
            // Never fail the lens over an outcome lookup — null means "fall back to the bridge".
            Logger.LogException(ex, $"RunTestCodeLensCallbackListener: GetOutcomeAsync threw for {typeFullName}.{methodName}");
            return Task.FromResult<RunTestOutcomeEntry?>(null);
        }
    }

    internal static RunTestOutcomeEntry ToEntry(MethodOutcome outcome) => ToEntry(outcome, isStale: false);

    internal static RunTestOutcomeEntry ToEntry(MethodOutcome outcome, bool isStale) => new(
        outcome.Aggregate.ToString(),
        outcome.Rows.Select(r => new RunTestOutcomeRow(
            r.DisplayName, r.Outcome.ToString(), r.DurationMs, r.ErrorMessage,
            r.Steps.Count, r.FailedStep?.Index, r.FailedStep?.StepText, r.FailedStep?.Outcome.ToString())).ToList(),
        outcome.LastUpdatedUtc,
        outcome.IsRunning,
        isStale);

    /// <summary>
    /// The store only ever hears about a method from an IDE-observed run. If it hasn't heard about
    /// this method in a while, a run it never saw may have happened since — a container-heuristic
    /// miss, a disabled logger, a CLI run — and the entry should stop indefinitely shadowing the
    /// reflection bridge's live check (fresh-eyes review finding: previously the store's answer was
    /// trusted forever, with no way back to the bridge short of a rebuild). Deliberately a separate
    /// check from <see cref="IsStale"/>, not folded into it: rebuild-staleness means "known wrong, and
    /// the bridge would say the same stale thing" (skip it), while aging out means "no longer vouched
    /// for" (the bridge might know something new — see the call site in <see cref="GetOutcomeAsync"/>,
    /// which treats an aged-out entry as absent rather than stale).
    /// </summary>
    internal static bool IsTooOldToTrust(MethodOutcome outcome, DateTime? nowUtc = null)
        => (nowUtc ?? DateTime.UtcNow) - outcome.LastUpdatedUtc > MaxTrustedAge;

    internal static readonly TimeSpan MaxTrustedAge = TimeSpan.FromHours(2);

    /// <summary>
    /// Outcomes recorded before the container was last built describe code that no longer exists;
    /// the lens shows them as stale (no pass/fail glyph, and no bridge fallback — VS's TestStore would
    /// just repeat the same stale value) rather than a confident green on rebuilt code. A missing
    /// container, or any failure determining the container's write time, counts as stale too
    /// (<see cref="TestOutcomeFreshness"/> — this used to be a second, independently-written copy of
    /// that rule that disagreed with it on the error path).
    /// </summary>
    internal static bool IsStale(MethodOutcome outcome)
        => !TestOutcomeFreshness.IsFresh(outcome.Key.Source, outcome.LastUpdatedUtc, TestOutcomeFreshness.DefaultSourceLastWriteUtc);
}
