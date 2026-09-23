#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>
/// Keeps <see cref="TestOutcomeStore"/>'s contents across server restarts in one JSON file under the
/// Reqnroll application directory (<c>%LOCALAPPDATA%\Reqnroll\test-outcomes.json</c> — a sibling of
/// the <c>logs</c> subfolder, not inside it; issue #726), keyed by test-container
/// path like the store itself — so no per-solution or per-IDE bookkeeping is needed: whichever solution
/// owns a container, its outcomes are found by the container's path. One file shared by every IDE the
/// server serves, since the server process (not the IDE) now owns this state.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Freshness.</b> An entry whose container assembly has been rebuilt since the outcome was
///   recorded (file write time newer than <c>LastUpdatedUtc</c>), or whose container no longer exists,
///   is dropped on load — a green glyph on rebuilt code would be a lie.</item>
///   <item><b>Retention.</b> Entries older than <see cref="MaxAge"/> are dropped on save.</item>
///   <item><b>Concurrency.</b> Several server instances (one per open VS/Rider/VS Code window) could in
///   principle share the file. Save merges into whatever is on disk at that moment (newest
///   <c>LastUpdatedUtc</c> per method wins) and writes via temp file + replace, so the worst case of two
///   simultaneous saves is one instance's last run being re-merged on its next save, never a torn file.</item>
///   <item><b>Size.</b> Rows are persisted without their raw stdout / stack trace — the parsed steps and
///   the error message are what the IDE renders.</item>
/// </list>
/// Every failure is logged and swallowed: persistence is a convenience, never a reason for a lens to fail.
/// </remarks>
public sealed class TestOutcomePersistence
{
    internal const int FormatVersion = 1;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly string _filePath;
    private readonly Func<string, DateTime?> _sourceLastWriteUtc;
    private readonly IIdeSupportLogger _logger;
    private readonly object _gate = new();

    /// <summary>DI entry point.</summary>
    public TestOutcomePersistence(IIdeSupportLogger logger)
        : this(ResolveDefaultFilePath(logger), TestOutcomeFreshness.DefaultSourceLastWriteUtc, logger)
    {
    }

    /// <summary>Test seam: explicit file and container-timestamp lookup.</summary>
    internal TestOutcomePersistence(string filePath, Func<string, DateTime?> sourceLastWriteUtc, IIdeSupportLogger logger)
    {
        _filePath = filePath;
        _sourceLastWriteUtc = sourceLastWriteUtc;
        _logger = logger;
    }

    /// <summary>
    /// Uses <see cref="Logging.ReqnrollLogPaths.ResolveApplicationDirectory"/>, not
    /// <c>ResolveLogDirectory</c> — this file is persisted state, not a log, so it belongs in the
    /// application directory's root alongside the telemetry <c>userid</c> file rather than under
    /// the <c>logs</c> subfolder (issue #726); it is also exempt from the 10-day log-retention
    /// pruning that directory gets. Resolution is broadly safe (its own file enumeration is
    /// already guarded) but still combines environment-dependent paths via
    /// <see cref="Path.Combine(string, string)"/>, which can throw on a sufficiently unusual
    /// environment; never let that take the whole outcome pipeline down.
    /// </summary>
    private static string ResolveDefaultFilePath(IIdeSupportLogger logger)
    {
        try
        {
            return Path.Combine(ReqnrollLogPaths.ResolveApplicationDirectory(), "test-outcomes.json");
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(TestOutcomePersistence)}: could not resolve the Reqnroll application directory; falling back to the temp directory");
            return Path.Combine(Path.GetTempPath(), "reqnroll-test-outcomes.json");
        }
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
            _logger.LogVerbose($"{nameof(TestOutcomePersistence)}: loaded {fresh.Count} fresh of {onDisk.Count} persisted method outcome(s) from {_filePath}");
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomePersistence)}: load failed; starting empty");
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
                _logger.LogVerbose($"{nameof(TestOutcomePersistence)}: saved {kept.Count} method outcome(s) to {_filePath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomePersistence)}: save failed");
        }
    }

    /// <summary>A persisted outcome is usable while its container still exists and hasn't been rebuilt since.</summary>
    internal bool IsFresh(MethodOutcome outcome)
        => TestOutcomeFreshness.IsFresh(outcome.Key.Source, outcome.LastUpdatedUtc, _sourceLastWriteUtc);

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
            _logger.LogWarning($"{nameof(TestOutcomePersistence)}: existing {_filePath} is unreadable ({ex.Message}); overwriting.");
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
            _logger.LogInfo($"{nameof(TestOutcomePersistence)}: ignoring {_filePath} with format version {root.Value<int?>("version")} (expected {FormatVersion})");
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
