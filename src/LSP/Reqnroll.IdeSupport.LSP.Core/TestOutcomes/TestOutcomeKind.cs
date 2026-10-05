#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>Pass/fail state of one test case (one Scenario, or one example row of an Outline).</summary>
public enum TestOutcomeKind
{
    None,
    Passed,
    Failed,
    Skipped,
    NotFound,
    /// <summary>Listed in a run's <c>runStart</c> but no result yet.</summary>
    Running,
}
