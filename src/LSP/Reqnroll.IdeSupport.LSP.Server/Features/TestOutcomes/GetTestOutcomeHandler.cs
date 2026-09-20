#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Handles the custom <c>reqnroll/testOutcomes/getOutcome</c> request: the Run CodeLens's outcome
/// lookup, moved here from the Visual Studio-only <c>RunTestCodeLensCallbackListener</c> so every IDE
/// gets the same staleness/trust rules instead of each re-implementing them.
/// </summary>
public sealed class GetTestOutcomeHandler
{
    /// <summary>
    /// The store only ever hears about a method from an IDE-observed run. If it hasn't heard about
    /// this method in a while, a run it never saw may have happened since — a container-heuristic
    /// miss, a disabled logger, a CLI run — and the entry should stop indefinitely shadowing the
    /// client's own reflection-bridge fallback. Deliberately a separate check from <see cref="IsStale"/>:
    /// rebuild-staleness means "known wrong, and the bridge would say the same stale thing" (skip it),
    /// while aging out means "no longer vouched for" (the bridge might know something new) — an
    /// aged-out entry is reported as <c>Found = false</c>, not stale-but-populated.
    /// </summary>
    internal static readonly TimeSpan MaxTrustedAge = TimeSpan.FromHours(2);

    private readonly TestOutcomeStore _store;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    public GetTestOutcomeHandler(TestOutcomeStore store, IIdeSupportLogger logger, IOperationDurationRecorder? recorder = null)
    {
        _store = store;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/testOutcomes/getOutcome</c> request.</summary>
    public Task<GetTestOutcomeResponse> HandleAsync(GetTestOutcomeParams request, CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollGetTestOutcome);

        try
        {
            var outcome = _store.TryGet(request.AssemblyPath, request.TypeFullName, request.MethodName);
            var aged = outcome is not null && !outcome.IsRunning && IsTooOldToTrust(outcome);
            if (aged)
            {
                _logger.LogVerbose($"{nameof(GetTestOutcomeHandler)}: {request.TypeFullName}.{request.MethodName} → aged out ({DateTime.UtcNow - outcome!.LastUpdatedUtc} since last seen); reporting not found.");
                return Task.FromResult(new GetTestOutcomeResponse { Found = false });
            }

            if (outcome is null)
                return Task.FromResult(new GetTestOutcomeResponse { Found = false });

            var stale = !outcome.IsRunning && IsStale(outcome);
            _logger.LogVerbose($"{nameof(GetTestOutcomeHandler)}: {request.TypeFullName}.{request.MethodName} → {outcome.Aggregate}{(outcome.IsRunning ? " (running)" : string.Empty)}{(stale ? " (stale)" : string.Empty)}");
            return Task.FromResult(ToResponse(outcome, stale));
        }
        catch (Exception ex)
        {
            // Never fail the lens over an outcome lookup — Found=false means "fall back to the bridge".
            _logger.LogException(ex, $"{nameof(GetTestOutcomeHandler)}: threw for {request.TypeFullName}.{request.MethodName}");
            return Task.FromResult(new GetTestOutcomeResponse { Found = false });
        }
    }

    internal static GetTestOutcomeResponse ToResponse(MethodOutcome outcome, bool isStale) => new()
    {
        Found = true,
        Aggregate = outcome.Aggregate.ToString(),
        Rows = outcome.Rows.Select(r => new TestOutcomeRowDto
        {
            DisplayName = r.DisplayName,
            Outcome = r.Outcome.ToString(),
            DurationMs = r.DurationMs,
            ErrorMessage = r.ErrorMessage,
            StepCount = r.Steps.Count,
            FailedStepIndex = r.FailedStep?.Index,
            FailedStepText = r.FailedStep?.StepText,
            FailedStepOutcome = r.FailedStep?.Outcome.ToString(),
        }).ToList(),
        LastUpdatedUtc = outcome.LastUpdatedUtc,
        IsRunning = outcome.IsRunning,
        IsStale = isStale,
    };

    internal static bool IsTooOldToTrust(MethodOutcome outcome, DateTime? nowUtc = null)
        => (nowUtc ?? DateTime.UtcNow) - outcome.LastUpdatedUtc > MaxTrustedAge;

    /// <summary>
    /// Outcomes recorded before the container was last built describe code that no longer exists;
    /// the lens shows them as stale (no pass/fail glyph, and no bridge fallback) rather than a
    /// confident green on rebuilt code. A missing container, or any failure determining the
    /// container's write time, counts as stale too (see <see cref="TestOutcomeFreshness"/>).
    /// </summary>
    internal static bool IsStale(MethodOutcome outcome)
        => !TestOutcomeFreshness.IsFresh(outcome.Key.Source, outcome.LastUpdatedUtc, TestOutcomeFreshness.DefaultSourceLastWriteUtc);
}
