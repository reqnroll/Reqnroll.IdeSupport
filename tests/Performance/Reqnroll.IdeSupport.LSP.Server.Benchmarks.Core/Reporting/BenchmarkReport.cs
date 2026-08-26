#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Latency;
using Reqnroll.IdeSupport.LSP.Server.Benchmarks.Scenarios;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Reporting;

/// <summary>
/// The full result of a benchmark run: the per-operation results plus the run context (machine,
/// timestamp, corpus fingerprint hash). Rendered to a console table and to JSON — the JSON is also
/// the Layer 3 baseline format (compare a future run's JSON against a stored baseline).
/// </summary>
public sealed record BenchmarkReport(
    string MachineName,
    bool AssertThresholds,
    DateTimeOffset TimestampUtc,
    string CorpusDescription,
    IReadOnlyList<OperationResult> Results,
    IReadOnlyList<SkippedBatchScenario>? Skipped = null,
    SessionStats? Session = null,
    string Transport = "in-process (in-memory pipe)",
    IReadOnlyList<ContentionCheck>? ContentionChecks = null)
{
    /// <summary>True when every asserted operation met its performance target.</summary>
    public bool AllPassed =>
        Results.All(r => r.MeetsTarget) && (ContentionChecks?.All(c => c.MeetsTarget) ?? true);

    public string ToConsoleTable() => ConsoleReporter.Render(this);

    public string ToJson() => JsonReporter.Render(this);
}

/// <summary>Renders a <see cref="BenchmarkReport"/> as a fixed-width console table.</summary>
public static class ConsoleReporter
{
    public static string Render(BenchmarkReport report)
    {
        var underLoad = report.Session is not null;
        var sb = new StringBuilder();
        sb.AppendLine(underLoad
            ? $"Performance benchmark (under concurrent load) — {report.MachineName} — {report.TimestampUtc:u}"
            : $"Performance benchmark — {report.MachineName} — {report.TimestampUtc:u}");
        sb.AppendLine($"Corpus: {report.CorpusDescription}");
        sb.AppendLine($"Transport: {report.Transport}");
        sb.AppendLine(report.AssertThresholds
            ? "Mode: ASSERT (reference machine — absolute performance thresholds enforced)"
            : underLoad
                ? "Mode: REPORT-ONLY (reality check — targets shown are isolated-case references, not a gate)"
                : "Mode: REPORT-ONLY (not a reference machine — numbers informational, exit 0)");
        sb.AppendLine();
        sb.AppendLine($"{"Operation",-40} {"Target",9} {"Stat",6} {"P50",9} {"P95",9} {"P99",9} {"Max",9}  Verdict");
        sb.AppendLine(new string('-', 110));

        var orderedResults = report.Results
            .OrderBy(r => r.Target.Operation, StringComparer.Ordinal)
            .ToList();

        foreach (var r in orderedResults)
        {
            var s = r.Summary;
            var target = r.Target.TargetMs > 0 ? $"{r.Target.TargetMs,7:0}ms" : $"{"—",9}";
            var verdict = !report.AssertThresholds ? "—" : (r.MeetsTarget ? "PASS" : "FAIL");
            var label = r.Target.IncludesFixedDelay ? r.Target.Operation + " *" : r.Target.Operation;
            sb.AppendLine(
                $"{Trunc(label, 40),-40} " +
                $"{target} {r.MeasuredStatistic,6} " +
                $"{s.P50Ms,7:0.0}ms {s.P95Ms,7:0.0}ms {s.P99Ms,7:0.0}ms {s.MaxMs,7:0.0}ms  {verdict}");
        }

        if (orderedResults.Any(r => r.Target.IncludesFixedDelay))
        {
            sb.AppendLine();
            sb.AppendLine("* includes a fixed debounce/coalescing window the pipeline deliberately waits out");
            sb.AppendLine("  before reacting (typically ~500ms) — not pure processing cost.");
        }

        if (report.Skipped is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Skipped (not measured):");
            foreach (var s in report.Skipped)
                sb.AppendLine($"  {s.Target.Operation,-40} — {s.Reason}");
        }

        if (report.ContentionChecks is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Dispatch-fairness / head-of-line blocking (cheap read latency vs. same-run solo");
            sb.AppendLine("baseline; a ratio, not absolute ms, since both swing with machine speed -- see #488):");
            foreach (var c in report.ContentionChecks)
            {
                var verdict = !report.AssertThresholds ? "—" : (c.MeetsTarget ? "PASS" : "FAIL");
                sb.AppendLine(
                    $"  {Trunc(c.Operation, 48),-48} baseline P95={c.Baseline.P95Ms,7:F1}ms  " +
                    $"under-load P95={c.UnderLoad.P95Ms,8:F1}ms  ratio={c.RatioAtP95,6:F1}x  " +
                    $"ceiling={c.CeilingRatio,4:F0}x  {verdict}");
            }
        }

        if (report.Session is { } session)
        {
            sb.AppendLine();
            sb.AppendLine("Session activity (one active document; bursts pipelined, some superseded):");
            sb.AppendLine($"  bursts={session.Bursts}  supersede-rate={session.SupersedeRate:0.##}  " +
                          $"think={session.ThinkMs}ms  typing-gap={session.TypingGapMs}ms");
            sb.AppendLine($"  requests issued={session.RequestsIssued}  cancelled={session.RequestsCancelled} " +
                          $"({session.CancellationRatePct:0.0}%)  mean-time-to-cancel={session.MeanTimeToCancelMs:0.0}ms");
        }

        sb.AppendLine();
        if (report.AssertThresholds)
            sb.AppendLine(report.AllPassed ? "RESULT: all targets met." : "RESULT: one or more targets MISSED.");
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Renders a <see cref="BenchmarkReport"/> as JSON (the Layer 3 baseline format).</summary>
public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(BenchmarkReport report) => JsonSerializer.Serialize(report, Options);
}
