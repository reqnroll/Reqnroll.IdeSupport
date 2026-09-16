using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Common.TestOutcomes;

/// <summary>Per-step outcome as reported by Reqnroll's default step trace.</summary>
public enum StepTraceOutcome
{
    /// <summary><c>-&gt; done: {match} ({d}s)</c></summary>
    Done,
    /// <summary><c>-&gt; error: {message} ({d}s)</c> — the step threw.</summary>
    Error,
    /// <summary><c>-&gt; skipped: {message}</c> — explicitly skipped (skip exception).</summary>
    Skipped,
    /// <summary><c>-&gt; skipped because of previous errors</c> — never ran.</summary>
    SkippedBecauseOfPreviousErrors,
    /// <summary><c>-&gt; pending: {match}: {message}</c></summary>
    Pending,
    /// <summary><c>-&gt; binding error: {message}</c> — ambiguous or unresolvable binding.</summary>
    BindingError,
    /// <summary><c>-&gt; undefined: …</c> — no binding matches; the skeleton follows on indented lines.</summary>
    Undefined,
}

/// <summary>
/// One executed (or skipped) step from a scenario's trace, in execution order. <see cref="Index"/> is
/// the position among the scenario's steps as Reqnroll ran them — Background steps first, then the
/// scenario's own — which is how it correlates to the <c>.feature</c> file: by order, not by text.
/// </summary>
public sealed class StepTraceEntry
{
    public StepTraceEntry(int index, string stepText, StepTraceOutcome outcome, string? detail, double? durationSeconds)
    {
        Index = index;
        StepText = stepText;
        Outcome = outcome;
        Detail = detail;
        DurationSeconds = durationSeconds;
    }

    /// <summary>0-based position in execution order.</summary>
    public int Index { get; }

    /// <summary>The step line as traced (keyword + text), or empty if the trace had an outcome with no step line before it.</summary>
    public string StepText { get; }

    public StepTraceOutcome Outcome { get; }

    /// <summary>Match text for <c>done</c>/<c>pending</c>, the message for <c>error</c>/<c>skipped</c>/<c>binding error</c>, the explanation + skeleton for <c>undefined</c>.</summary>
    public string? Detail { get; }

    /// <summary>Only <c>done</c> and <c>error</c> carry a duration.</summary>
    public double? DurationSeconds { get; }

    public bool IsFailure => Outcome is StepTraceOutcome.Error or StepTraceOutcome.BindingError or StepTraceOutcome.Undefined;

    public override string ToString() => $"[{Index}] {Outcome}: {StepText}";
}

/// <summary>
/// Parses Reqnroll's default step trace (what <c>Reqnroll.Tracing.TestTracer</c> writes through
/// <c>DefaultListener</c>: step lines as plain test output, outcomes as <c>-&gt; </c>-prefixed tool output)
/// out of a test case's captured stdout. This — not the stack trace — is the reliable per-step signal
/// (Test-Runner-Integration-Design §6: every step's <c>#line</c> is followed by <c>#line hidden</c>, so
/// exception frames always point at the scenario's last step).
/// </summary>
/// <remarks>
/// <para>
/// Formats handled (decompiled from Reqnroll 3.3.4's <c>TestTracer</c>): the seven outcomes in
/// <see cref="StepTraceOutcome"/>; <c>-&gt; duration:</c>, <c>-&gt; warning:</c>, <c>-&gt; Loading plugin</c>,
/// <c>-&gt; Using default config</c> and any other tool output are ignored; ANSI colour codes are
/// stripped; the decimal separator in durations may be <c>.</c> or <c>,</c> (Reqnroll formats with the
/// current culture); MSTest's <c>TestContext Messages:</c> header and blank lines are ignored.
/// </para>
/// <para>
/// A step's text is the line Reqnroll traced for it. Between one outcome and the next there may be
/// other test output too — hook output before the step, a binding's own <c>Console.WriteLine</c> after
/// it, table/doc-string arguments (indented) — so the step line is chosen as: the first non-indented
/// candidate that starts with a Gherkin step keyword (English + <c>*</c>), else the last non-indented
/// candidate. Correlation to the feature file should use <see cref="StepTraceEntry.Index"/> anyway; the
/// text is for display.
/// </para>
/// </remarks>
public static class StepTraceParser
{
    private const string ToolOutputPrefix = "-> ";

    private static readonly Regex AnsiEscape = new("\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    // "{text} ({d}s)" — the duration is the trailing parenthesised number; the text may itself contain parentheses.
    private static readonly Regex TrailingDuration = new(@"^(?<text>.*)\s\((?<seconds>\d+(?:[.,]\d+)?)s\)\s*$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly string[] EnglishKeywords = { "Given ", "When ", "Then ", "And ", "But ", "* " };

    public static IReadOnlyList<StepTraceEntry> Parse(string? stdout)
    {
        var entries = new List<StepTraceEntry>();
        if (string.IsNullOrEmpty(stdout)) return entries;

        var lines = AnsiEscape.Replace(stdout!, string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var candidates = new List<string>();
        StringBuilder? undefinedSkeleton = null;
        StepTraceEntry? pendingUndefined = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.StartsWith(ToolOutputPrefix, StringComparison.Ordinal))
            {
                FlushUndefined();
                var tool = line.Substring(ToolOutputPrefix.Length);
                if (TryParseOutcome(tool, out var outcome, out var detail, out var seconds))
                {
                    var entry = new StepTraceEntry(entries.Count, PickStepText(candidates), outcome, detail, seconds);
                    entries.Add(entry);
                    candidates.Clear();
                    if (outcome == StepTraceOutcome.Undefined)
                    {
                        // The skeleton follows on indented lines; gather it into the detail.
                        pendingUndefined = entry;
                        undefinedSkeleton = new StringBuilder(detail);
                    }
                }
                // Other tool output (duration:, warning:, Loading plugin, scope-mismatch preamble…) is noise here.
                continue;
            }

            if (pendingUndefined is not null)
            {
                if (line.Length == 0 || char.IsWhiteSpace(raw[0]))
                {
                    undefinedSkeleton!.Append('\n').Append(line);
                    continue;
                }
                FlushUndefined();
            }

            if (line.Length == 0) continue;
            candidates.Add(raw.TrimEnd());
        }

        FlushUndefined();
        return entries;

        void FlushUndefined()
        {
            if (pendingUndefined is null) return;
            var index = entries.IndexOf(pendingUndefined);
            entries[index] = new StepTraceEntry(pendingUndefined.Index, pendingUndefined.StepText, pendingUndefined.Outcome,
                undefinedSkeleton!.ToString().TrimEnd(), pendingUndefined.DurationSeconds);
            pendingUndefined = null;
            undefinedSkeleton = null;
        }
    }

    private static string PickStepText(List<string> candidates)
    {
        string? last = null;
        foreach (var candidate in candidates)
        {
            if (char.IsWhiteSpace(candidate[0])) continue; // argument / continuation line
            last = candidate;
            foreach (var keyword in EnglishKeywords)
                if (candidate.StartsWith(keyword, StringComparison.Ordinal))
                    return candidate;
        }
        return last ?? string.Empty;
    }

    private static bool TryParseOutcome(string tool, out StepTraceOutcome outcome, out string? detail, out double? seconds)
    {
        seconds = null;
        detail = null;

        if (tool.Equals("skipped because of previous errors", StringComparison.Ordinal))
        {
            outcome = StepTraceOutcome.SkippedBecauseOfPreviousErrors;
            return true;
        }
        if (TryPrefix(tool, "done: ", out var rest))
        {
            outcome = StepTraceOutcome.Done;
            (detail, seconds) = SplitDuration(rest);
            return true;
        }
        if (TryPrefix(tool, "error: ", out rest))
        {
            outcome = StepTraceOutcome.Error;
            (detail, seconds) = SplitDuration(rest);
            return true;
        }
        if (TryPrefix(tool, "skipped: ", out rest))
        {
            outcome = StepTraceOutcome.Skipped;
            detail = rest;
            return true;
        }
        if (TryPrefix(tool, "pending: ", out rest))
        {
            outcome = StepTraceOutcome.Pending;
            detail = rest;
            return true;
        }
        if (TryPrefix(tool, "binding error: ", out rest))
        {
            outcome = StepTraceOutcome.BindingError;
            detail = rest;
            return true;
        }
        if (TryPrefix(tool, "undefined: ", out rest))
        {
            outcome = StepTraceOutcome.Undefined;
            detail = rest;
            return true;
        }

        outcome = default;
        return false;
    }

    private static bool TryPrefix(string text, string prefix, out string rest)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = text.Substring(prefix.Length);
            return true;
        }
        rest = string.Empty;
        return false;
    }

    private static (string Text, double? Seconds) SplitDuration(string text)
    {
        var match = TrailingDuration.Match(text);
        if (!match.Success) return (text, null);
        var number = match.Groups["seconds"].Value.Replace(',', '.');
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? (match.Groups["text"].Value, seconds)
            : (text, null);
    }
}
