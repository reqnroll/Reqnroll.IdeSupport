#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>Last-known state of one row (test case) of a method.</summary>
public sealed record RowOutcome(
    string DisplayName,
    TestOutcomeKind Outcome,
    double DurationMs,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? Stdout,
    bool StdoutTruncated,
    string RunId,
    DateTime RecordedUtc,
    /// <summary>Reqnroll's step trace parsed out of <see cref="Stdout"/> (execution order); empty when the output carried none.</summary>
    IReadOnlyList<StepTraceEntry> Steps)
{
    /// <summary>The first step that failed (error / binding error / undefined), or null.</summary>
    public StepTraceEntry? FailedStep => Steps.FirstOrDefault(s => s.IsFailure);
}
