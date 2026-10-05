#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>Aggregate + rows for one generated test method.</summary>
public sealed record MethodOutcome(
    TestOutcomeKey Key,
    TestOutcomeKind Aggregate,
    IReadOnlyList<RowOutcome> Rows,
    DateTime LastUpdatedUtc,
    /// <summary>True between a run's <c>runStart</c> naming this method and that run's completion.</summary>
    bool IsRunning = false)
{
    public int FailedRowCount => Rows.Count(r => r.Outcome == TestOutcomeKind.Failed);
}
