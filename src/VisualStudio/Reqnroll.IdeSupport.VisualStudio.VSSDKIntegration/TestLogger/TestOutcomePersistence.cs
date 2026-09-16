using System.ComponentModel.Composition;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// Keeps <see cref="TestOutcomeStore"/>'s contents across VS sessions in one JSON file under the Reqnroll
/// log directory (<c>%LOCALAPPDATA%\Reqnroll\test-outcomes.json</c>), keyed by test-container path like
/// the store itself — so no per-solution bookkeeping is needed: whichever solution owns a container,
/// its outcomes are found by the container's path.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Freshness.</b> An entry whose container assembly has been rebuilt since the outcome was
///   recorded (file write time newer than <c>LastUpdatedUtc</c>), or whose container no longer exists,
///   is dropped on load — a green glyph on rebuilt code would be a lie VS's own lens happily tells.</item>
///   <item><b>Retention.</b> Entries older than <see cref="MaxAge"/> are dropped on save.</item>
///   <item><b>Concurrency.</b> Several VS instances share the file. Save merges into whatever is on disk
///   at that moment (newest <c>LastUpdatedUtc</c> per method wins) and writes via temp file + replace,
///   so the worst case of two simultaneous saves is one instance's last run being re-merged on its next
///   save, never a torn file.</item>
///   <item><b>Size.</b> Rows are persisted without their raw stdout / stack trace — the parsed steps and
///   the error message are what the IDE renders.</item>
/// </list>
/// Every failure is logged and swallowed: persistence is a convenience, never a reason for a lens to fail.
/// </remarks>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class TestOutcomePersistence
{
    internal const int FormatVersion = 1;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly IIdeSupportLogger Logger = new SynchronousFileLogger("vs", "ext", TraceLevel.Verbose);

    private readonly string _filePath;
    private readonly Func<string, DateTime?> _sourceLastWriteUtc;
    private readonly object _gate = new();

    [ImportingConstructor]
    public TestOutcomePersistence()
        : this(Path.Combine(ReqnrollLogPaths.ResolveLogDirectory(), "test-outcomes.json"), DefaultSourceLastWriteUtc)
    {
    }

    /// <summary>Test seam: explicit file and container-timestamp lookup.</summary>
    internal TestOutcomePersistence(string filePath, Func<string, DateTime?> sourceLastWriteUtc)
    {
        _filePath = filePath;
        _sourceLastWriteUtc = sourceLastWriteUtc;
    }

    public string FilePath => _filePath;

    /// <summary>Reads the file and returns the still-fresh entries; empty on any problem.</summary>
    public IReadOnlyList<MethodOutcome> Load()
    {
        try
        {
            List<MethodOutcome> onDisk;
            lock (_gate) onDisk = ReadFile();
            var fresh = onDisk.Where(IsFresh).ToList();
            Logger.LogVerbose($"{nameof(TestOutcomePersistence)}: loaded {fresh.Count} fresh of {onDisk.Count} persisted method outcome(s) from {_filePath}");
            return fresh;
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, $"{nameof(TestOutcomePersistence)}: load failed; starting empty");
            return Array.Empty<MethodOutcome>();
        }
    }

    /// <summary>Merges <paramref name="outcomes"/> into the file (newest per method wins) and prunes stale/old entries.</summary>
    public void Save(IEnumerable<MethodOutcome> outcomes, DateTime? nowUtc = null)
    {
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            lock (_gate)
            {
                var merged = new Dictionary<TestOutcomeKey, MethodOutcome>(TestOutcomeKey.Comparer);
                foreach (var existing in ReadFileOrEmpty())
                    merged[existing.Key] = existing;
                foreach (var outcome in outcomes)
                {
                    if (outcome.Rows.Count == 0) continue;
                    if (!merged.TryGetValue(outcome.Key, out var current) || current.LastUpdatedUtc <= outcome.LastUpdatedUtc)
                        merged[outcome.Key] = outcome;
                }

                var kept = merged.Values
                    .Where(m => now - m.LastUpdatedUtc <= MaxAge && IsFresh(m))
                    .OrderBy(m => m.Key.Source, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Key.TypeFullName, StringComparer.Ordinal).ThenBy(m => m.Key.MethodName, StringComparer.Ordinal)
                    .ToList();

                WriteFile(kept);
                Logger.LogVerbose($"{nameof(TestOutcomePersistence)}: saved {kept.Count} method outcome(s) to {_filePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, $"{nameof(TestOutcomePersistence)}: save failed");
        }
    }

    /// <summary>A persisted outcome is usable while its container still exists and hasn't been rebuilt since.</summary>
    internal bool IsFresh(MethodOutcome outcome)
    {
        var written = _sourceLastWriteUtc(outcome.Key.Source);
        return written is not null && written.Value <= outcome.LastUpdatedUtc;
    }

    private static DateTime? DefaultSourceLastWriteUtc(string source)
    {
        try
        {
            return File.Exists(source) ? File.GetLastWriteTimeUtc(source) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- file format (hand-mapped so the on-disk shape is explicit and independent of the record types) ----

    /// <summary>
    /// For <see cref="Save"/>: a corrupt file on disk must not block saving forever — it is treated as
    /// empty and overwritten (the corruption is logged once per save until it is gone).
    /// </summary>
    private List<MethodOutcome> ReadFileOrEmpty()
    {
        try
        {
            return ReadFile();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"{nameof(TestOutcomePersistence)}: existing {_filePath} is unreadable ({ex.Message}); overwriting.");
            return new List<MethodOutcome>();
        }
    }

    private List<MethodOutcome> ReadFile()
    {
        var result = new List<MethodOutcome>();
        if (!File.Exists(_filePath)) return result;

        var root = JObject.Parse(File.ReadAllText(_filePath));
        if ((root.Value<int?>("version") ?? 0) != FormatVersion)
        {
            Logger.LogInfo($"{nameof(TestOutcomePersistence)}: ignoring {_filePath} with format version {root.Value<int?>("version")} (expected {FormatVersion})");
            return result;
        }

        foreach (var m in root["methods"] as JArray ?? new JArray())
        {
            var source = m.Value<string>("source");
            var type = m.Value<string>("type");
            var method = m.Value<string>("method");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(type) || string.IsNullOrEmpty(method)) continue;
            var key = new TestOutcomeKey(source!, type!, method!);
            var updated = m.Value<DateTime?>("updatedUtc") ?? DateTime.MinValue;

            var rows = new List<RowOutcome>();
            foreach (var r in m["rows"] as JArray ?? new JArray())
            {
                var steps = (r["steps"] as JArray ?? new JArray())
                    .Select((s, i) => new StepTraceEntry(
                        i,
                        s.Value<string>("text") ?? string.Empty,
                        Enum.TryParse<StepTraceOutcome>(s.Value<string>("outcome"), out var so) ? so : StepTraceOutcome.Done,
                        s.Value<string>("detail"),
                        s.Value<double?>("seconds")))
                    .ToList();
                rows.Add(new RowOutcome(
                    r.Value<string>("displayName") ?? string.Empty,
                    TestOutcomeStore.ParseOutcome(r.Value<string>("outcome")),
                    r.Value<double?>("durationMs") ?? 0,
                    r.Value<string>("errorMessage"),
                    ErrorStackTrace: null,
                    Stdout: null,
                    StdoutTruncated: false,
                    r.Value<string>("runId") ?? string.Empty,
                    r.Value<DateTime?>("recordedUtc") ?? updated,
                    steps));
            }
            if (rows.Count == 0) continue;
            result.Add(new MethodOutcome(key, TestOutcomeStore.Aggregate(rows), rows, updated));
        }
        return result;
    }

    private void WriteFile(IReadOnlyList<MethodOutcome> outcomes)
    {
        var root = new JObject
        {
            ["version"] = FormatVersion,
            ["methods"] = new JArray(outcomes.Select(m => new JObject
            {
                ["source"] = m.Key.Source,
                ["type"] = m.Key.TypeFullName,
                ["method"] = m.Key.MethodName,
                ["updatedUtc"] = m.LastUpdatedUtc,
                ["rows"] = new JArray(m.Rows.Select(r => new JObject
                {
                    ["displayName"] = r.DisplayName,
                    ["outcome"] = r.Outcome.ToString(),
                    ["durationMs"] = r.DurationMs,
                    ["errorMessage"] = r.ErrorMessage,
                    ["runId"] = r.RunId,
                    ["recordedUtc"] = r.RecordedUtc,
                    ["steps"] = new JArray(r.Steps.Select(s => new JObject
                    {
                        ["text"] = s.StepText,
                        ["outcome"] = s.Outcome.ToString(),
                        ["detail"] = s.Detail,
                        ["seconds"] = s.DurationSeconds,
                    })),
                })),
            })),
        };

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Parameterless ToString(): JToken.ToString(Formatting) is a known MissingMethodException in
            // the VS host (see the repo memory on Newtonsoft in devenv).
            File.WriteAllText(temp, root.ToString());
            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, destinationBackupFileName: null);
            else
                File.Move(temp, _filePath);
        }
        finally
        {
            // Replace/Move failed (another instance mid-read, AV scan…): don't leave the temp file behind.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
        }
    }
}
