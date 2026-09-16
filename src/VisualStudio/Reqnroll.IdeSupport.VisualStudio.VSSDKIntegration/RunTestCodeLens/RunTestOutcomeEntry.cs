#nullable enable

using System;
using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// One row (test case) of a method's last-known outcome, as shipped from devenv.exe to the OOP CodeLens
/// host over <c>ICodeLensCallbackService</c>. Plain data, JSON-serializable by StreamJsonRpc, same
/// discipline as <see cref="RunTestTargetEntry"/>.
/// </summary>
public sealed record RunTestOutcomeRow(
    string DisplayName,
    /// <summary><c>Passed</c>/<c>Failed</c>/<c>Skipped</c>/<c>NotFound</c>/<c>None</c> — the <c>TestOutcomeKind</c> name.</summary>
    string Outcome,
    double DurationMs,
    string? ErrorMessage,
    /// <summary>Number of steps Reqnroll traced for this row (0 when the output carried no trace).</summary>
    int StepCount = 0,
    /// <summary>0-based execution index of the first failing step, or null.</summary>
    int? FailedStepIndex = null,
    /// <summary>The traced text of that step (keyword + text), or null.</summary>
    string? FailedStepText = null,
    /// <summary><c>Error</c>/<c>BindingError</c>/<c>Undefined</c> — the <c>StepTraceOutcome</c> name, or null.</summary>
    string? FailedStepOutcome = null);

/// <summary>
/// Last-known outcome of one generated test method from the in-proc <c>TestOutcomeStore</c> (fed by the
/// bundled VSTest logger), for the Run CodeLens glyph and details pane. Null from the callback means
/// "no run reported this method this session" and the data point falls back to the reflection bridge.
/// </summary>
public sealed record RunTestOutcomeEntry(
    /// <summary>Aggregate over <see cref="Rows"/>: <c>Failed</c> &gt; <c>Passed</c> &gt; <c>Skipped</c> &gt; other.</summary>
    string Aggregate,
    IReadOnlyList<RunTestOutcomeRow> Rows,
    DateTime LastUpdatedUtc,
    /// <summary>A run naming this method has started and not yet completed.</summary>
    bool IsRunning = false,
    /// <summary>The container assembly was rebuilt after these outcomes were recorded; they describe old code.</summary>
    bool IsStale = false);
