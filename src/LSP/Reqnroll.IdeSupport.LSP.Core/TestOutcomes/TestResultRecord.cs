#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>One test case's result as received from the logger.</summary>
public sealed record TestResultRecord(
    string RunId,
    string Source,
    string? ManagedType,
    string? ManagedMethod,
    string FullyQualifiedName,
    string DisplayName,
    TestOutcomeKind Outcome,
    double DurationMs,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? Stdout,
    bool StdoutTruncated);
