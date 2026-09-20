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

    private readonly IIdeSupportLogger _logger;

    // This class runs in-process in devenv.exe (unlike its OOP counterpart RunTestCodeLensDataPoint,
    // which runs under a different process/PID entirely and genuinely needs its own logger instance),
    // so it imports the same shared IIdeSupportLogger MEF export every other devenv.exe-side
    // component uses.
    [ImportingConstructor]
    public RunTestCodeLensCallbackListener(IIdeSupportLogger logger)
    {
        _logger = logger;
    }

    [JsonRpcMethod(GetTargetsForLineMethod)]
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsForLineAsync(string fileUri, int line, CancellationToken cancellationToken)
    {
        _logger.LogVerbose($"RunTestCodeLensCallbackListener: GetTargetsForLineAsync called for {fileUri}:{line}");

        var fetch = RunTestCodeLensRedirect.GetTargetsForLineAsync;
        if (fetch is null)
        {
            _logger.LogWarning("RunTestCodeLensCallbackListener: RunTestCodeLensRedirect.GetTargetsForLineAsync is null — LSP connection not wired up yet; returning empty.");
            return Array.Empty<RunTestTargetEntry>();
        }

        try
        {
            var entries = await fetch(fileUri, line, cancellationToken).ConfigureAwait(false);
            _logger.LogVerbose($"RunTestCodeLensCallbackListener: GetTargetsForLineAsync returning {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} for {fileUri}:{line}");
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"RunTestCodeLensCallbackListener: GetTargetsForLineAsync threw for {fileUri}:{line}");
            throw;
        }
    }

    [JsonRpcMethod(GetOutcomeMethod)]
    public async Task<RunTestOutcomeEntry?> GetOutcomeAsync(string assemblyPath, string typeFullName, string methodName, CancellationToken cancellationToken)
    {
        var fetch = RunTestCodeLensRedirect.GetTestOutcomeAsync;
        if (fetch is null)
        {
            _logger.LogVerbose("RunTestCodeLensCallbackListener: GetOutcomeAsync — LSP connection not wired up yet; falling back to the bridge.");
            return null;
        }

        try
        {
            var outcome = await fetch(assemblyPath, typeFullName, methodName, cancellationToken).ConfigureAwait(false);
            _logger.LogVerbose($"RunTestCodeLensCallbackListener: GetOutcomeAsync {typeFullName}.{methodName} → {outcome?.Aggregate ?? "(none)"}{(outcome?.IsRunning == true ? " (running)" : string.Empty)}{(outcome?.IsStale == true ? " (stale)" : string.Empty)}");
            return outcome;
        }
        catch (Exception ex)
        {
            // Never fail the lens over an outcome lookup — null means "fall back to the bridge".
            _logger.LogException(ex, $"RunTestCodeLensCallbackListener: GetOutcomeAsync threw for {typeFullName}.{methodName}");
            return null;
        }
    }
}
