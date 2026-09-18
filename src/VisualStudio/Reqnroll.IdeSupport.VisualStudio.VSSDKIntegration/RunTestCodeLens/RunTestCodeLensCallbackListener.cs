#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using Reqnroll.IdeSupport.Common.Logging;
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
    /// Last-known outcome of one generated test method from the LSP server's <c>TestOutcomeStore</c>
    /// (fed by the bundled VSTest logger — implementation plan §4.2). Null when no run has reported it.
    /// </summary>
    public const string GetOutcomeMethod = "Reqnroll.RunTestCodeLens.GetOutcome";

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
    public async Task<RunTestOutcomeEntry?> GetOutcomeAsync(string assemblyPath, string typeFullName, string methodName, CancellationToken cancellationToken)
    {
        var fetch = RunTestCodeLensRedirect.GetTestOutcomeAsync;
        if (fetch is null)
        {
            Logger.LogVerbose("RunTestCodeLensCallbackListener: GetOutcomeAsync — LSP connection not wired up yet; falling back to the bridge.");
            return null;
        }

        try
        {
            // Staleness/age-out are now computed server-side (GetTestOutcomeHandler) — the LSP
            // server owns the store, so it's the one place that can compare an outcome's timestamp
            // against the container's write time without a second, IDE-side copy of that rule.
            var outcome = await fetch(assemblyPath, typeFullName, methodName, cancellationToken).ConfigureAwait(false);
            Logger.LogVerbose($"RunTestCodeLensCallbackListener: GetOutcomeAsync {typeFullName}.{methodName} → {outcome?.Aggregate ?? "(none)"}{(outcome?.IsRunning == true ? " (running)" : string.Empty)}{(outcome?.IsStale == true ? " (stale)" : string.Empty)}");
            return outcome;
        }
        catch (Exception ex)
        {
            // Never fail the lens over an outcome lookup — null means "fall back to the bridge".
            Logger.LogException(ex, $"RunTestCodeLensCallbackListener: GetOutcomeAsync threw for {typeFullName}.{methodName}");
            return null;
        }
    }
}
