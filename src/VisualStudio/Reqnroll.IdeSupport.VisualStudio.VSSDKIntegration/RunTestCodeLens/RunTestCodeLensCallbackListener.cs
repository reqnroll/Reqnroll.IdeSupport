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
}
