#nullable enable
using System.Diagnostics;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Property-key names for the characteristic properties command events carry (issue #849).
/// Every value is a count, a bucket, a flag or a member of a small closed set — never a path or text.
/// Keys that predate this class (<c>LocationCount</c>, <c>UsagesCount</c>, ...) remain literals at
/// their emit sites; new keys are added here so a typo cannot silently fork a column.
/// </summary>
public static class TelemetryProperties
{
    /// <summary>Resolution state of the step at the cursor; one of the <see cref="StepStatus"/> values.</summary>
    public const string Status = "Status";
    /// <summary>The LSP request that carried the command (<c>textDocument/definition</c>, <c>reqnroll/findStepDefinitions</c>, ...).</summary>
    public const string Protocol = "Protocol";
    /// <summary>Distinct <c>.feature</c> files in a Find Step Usages result.</summary>
    public const string FileCount = "FileCount";
    /// <summary>Wall-clock time of the request, bucketed by <see cref="TelemetryBuckets.Duration(TimeSpan)"/>.</summary>
    public const string DurationBucket = "DurationBucket";
    /// <summary>Where a rename started; one of the <see cref="RenameOrigin"/> values.</summary>
    public const string Origin = "Origin";
    /// <summary>Step occurrences in <c>.feature</c> files a rename rewrote.</summary>
    public const string OccurrenceCount = "OccurrenceCount";
    /// <summary>Valid step definitions considered by Find Unused Step Definitions (distinct across projects).</summary>
    public const string TotalStepDefinitions = "TotalStepDefinitions";
    /// <summary>Compact key-sorted JSON object (string) of lookup-feature counts in a <c>FeatureUsageSummary</c> window.</summary>
    public const string LookupCounts = "LookupCounts";
    /// <summary>Compact key-sorted JSON object (string) of passive-feature counts in a <c>FeatureUsageSummary</c> window.</summary>
    public const string PassiveCounts = "PassiveCounts";
    /// <summary>Compact key-sorted JSON object (string) of gauge high-water marks (e.g. <c>UndefinedStepsPeak</c>, the most undefined steps any one document showed) in a <c>FeatureUsageSummary</c> window. Maxima, not counts: never sum them across windows.</summary>
    public const string PeakCounts = "PeakCounts";
    /// <summary>Seconds covered by a <c>FeatureUsageSummary</c> window.</summary>
    public const string WindowSeconds = "WindowSeconds";
    /// <summary><c>true</c> for the best-effort flush at graceful shutdown.</summary>
    public const string IsFinal = "IsFinal";
    /// <summary>Per-<c>SessionId</c> monotonically increasing number of emitted <c>FeatureUsageSummary</c> events; a gap marks a lost flush.</summary>
    public const string Sequence = "Sequence";
    /// <summary>Seconds since the flush service started (≈ server uptime), the denominator for a window's counts.</summary>
    public const string SessionSeconds = "SessionSeconds";
    /// <summary>Number of test targets a Run lens lookup resolved.</summary>
    public const string TargetCount = "TargetCount";
    /// <summary>What the Run lens lookup was for; one of the <see cref="TestTargetKind"/> values.</summary>
    public const string Kind = "Kind";
    /// <summary>Hooks a Find Hooks lookup returned.</summary>
    public const string HookCount = "HookCount";
    /// <summary>Scenarios a Go To Matching Scenarios lookup returned.</summary>
    public const string MatchCount = "MatchCount";
    /// <summary>Requested comment mode (<c>Toggle</c>, <c>Comment</c> or <c>Uncomment</c>).</summary>
    public const string Mode = "Mode";
    /// <summary>Lines the comment command covered, bucketed by <see cref="TelemetryBuckets.LineCount(int)"/>.</summary>
    public const string LineCountBucket = "LineCountBucket";
    /// <summary>Text edits a formatting request returned that actually change the text (0 = already formatted).</summary>
    public const string EditCount = "EditCount";
    /// <summary>Document length in lines, bucketed by <see cref="TelemetryBuckets.LineCount(int)"/>.</summary>
    public const string DocumentLineBucket = "DocumentLineBucket";
    /// <summary>Class name (no namespace, no stack trace) of the first Reqnroll frame an exception passed through.</summary>
    public const string Source = "Source";

    /// <summary>Values of <see cref="Status"/>.</summary>
    public static class StepStatus
    {
        /// <summary>Exactly one binding matched and at least one is navigable.</summary>
        public const string Bound = "Bound";
        /// <summary>More than one binding matched.</summary>
        public const string Ambiguous = "Ambiguous";
        /// <summary>No binding matched.</summary>
        public const string Undefined = "Undefined";
        /// <summary>A binding matched but none has a source location on this machine.</summary>
        public const string Unresolved = "Unresolved";
    }

    /// <summary>Values of <see cref="Origin"/>.</summary>
    public static class RenameOrigin
    {
        /// <summary>Rename invoked on a step in a <c>.feature</c> file.</summary>
        public const string Feature = "Feature";
        /// <summary>Rename invoked on a binding in C# source.</summary>
        public const string CSharpBinding = "CSharpBinding";
    }

    /// <summary>Values of <see cref="Kind"/>.</summary>
    public static class TestTargetKind
    {
        /// <summary>A plain Scenario.</summary>
        public const string Scenario = "Scenario";
        /// <summary>A Scenario Outline as a whole.</summary>
        public const string Outline = "Outline";
        /// <summary>One row of an Examples table.</summary>
        public const string ExampleRow = "ExampleRow";
        /// <summary>A whole Feature.</summary>
        public const string Feature = "Feature";
        /// <summary>A Rule.</summary>
        public const string Rule = "Rule";
    }
}

/// <summary>Closed-set bucketing for numeric telemetry values (issue #849), so cardinality stays bounded.</summary>
public static class TelemetryBuckets
{
    /// <summary>
    /// Buckets a duration with the same scheme <c>PerfSample</c>'s <c>DurationBucket</c> uses
    /// (<c>&lt;=10</c> … <c>&lt;=5000</c>, <c>&gt;5000</c>, in milliseconds), so the two are directly comparable.
    /// </summary>
    public static string Duration(TimeSpan elapsed) =>
        Performance.OperationDurationRecorder.Bucket(elapsed.TotalMilliseconds);

    /// <summary>Buckets the elapsed time since a <see cref="Stopwatch.GetTimestamp"/> value (see <see cref="Duration"/>).</summary>
    public static string DurationSince(long startTimestamp) =>
        Duration(Stopwatch.GetElapsedTime(startTimestamp));

    /// <summary>Buckets a line count: <c>0</c>, <c>1</c>, <c>2-10</c>, <c>11-50</c>, <c>51-200</c>, <c>201-1000</c>, <c>1000+</c>.</summary>
    public static string LineCount(int lines) => lines switch
    {
        <= 0 => "0",
        1 => "1",
        <= 10 => "2-10",
        <= 50 => "11-50",
        <= 200 => "51-200",
        <= 1000 => "201-1000",
        _ => "1000+",
    };
}
