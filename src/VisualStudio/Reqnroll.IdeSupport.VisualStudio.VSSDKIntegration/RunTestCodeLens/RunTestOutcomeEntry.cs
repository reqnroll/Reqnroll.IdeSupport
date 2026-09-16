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
    string? ErrorMessage);

/// <summary>
/// Last-known outcome of one generated test method from the in-proc <c>TestOutcomeStore</c> (fed by the
/// bundled VSTest logger), for the Run CodeLens glyph and details pane. Null from the callback means
/// "no run reported this method this session" and the data point falls back to the reflection bridge.
/// </summary>
public sealed record RunTestOutcomeEntry(
    /// <summary>Aggregate over <see cref="Rows"/>: <c>Failed</c> &gt; <c>Passed</c> &gt; <c>Skipped</c> &gt; other.</summary>
    string Aggregate,
    IReadOnlyList<RunTestOutcomeRow> Rows,
    DateTime LastUpdatedUtc);
