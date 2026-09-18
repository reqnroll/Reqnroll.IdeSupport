#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Server-side aggregate of test outcomes received from the bundled VSTest logger, keyed by generated
/// test method. Shared by every connected IDE (design: LSP-server outcome pipeline refactor) — the
/// receiving TCP listener, this store, and persistence used to live one-per-IDE inside the Visual
/// Studio extension (VSSDKIntegration); moving them here gives Rider and VS Code the same
/// implementation instead of each growing its own.
/// </summary>
/// <remarks>
/// Aggregation rule per method: <c>Failed</c> if any row failed; else <c>Passed</c> if any row passed;
/// else <c>Skipped</c> if any row skipped; else whatever the rows say (<c>NotFound</c>/<c>None</c>).
/// Rows are upserted by display name, so a row-filtered run (one example of an outline) refreshes that
/// row without erasing the other rows' last-known state.
/// <para>
/// Pure and synchronous; thread-safe via one lock. No IDE-specific types.
/// </para>
/// </remarks>
public sealed class TestOutcomeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TestOutcomeKey, MutableMethod> _methods = new(TestOutcomeKey.Comparer);
    private int _revision;

    /// <summary>Test-visible: an empty store.</summary>
    public TestOutcomeStore()
    {
    }

    /// <summary>
    /// DI entry point: seeds the store from the last session's persisted outcomes so the Run lens shows
    /// last-run state right after the server starts. Load failures leave the store empty and are logged
    /// by the persistence layer, never thrown here.
    /// </summary>
    public TestOutcomeStore(TestOutcomePersistence persistence)
    {
        if (persistence is null) return;
        var loaded = persistence.Load();
        if (loaded.Count > 0)
            Import(loaded);
    }

    /// <summary>Monotonic change counter; bumped on every mutation. Used to version CodeLens descriptors.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Raised synchronously on the recording thread after each mutation.</summary>
    public event EventHandler<TestOutcomesChangedEventArgs>? Changed;

    /// <summary>Records one result. Returns the affected key, or null if the record carried no usable identity.</summary>
    public TestOutcomeKey? Record(TestResultRecord result, DateTime? nowUtc = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        var key = TestOutcomeKey.From(result.Source, result.ManagedType, result.ManagedMethod, result.FullyQualifiedName);
        if (key is null) return null;

        var when = nowUtc ?? DateTime.UtcNow;
        // The raw stdout/stack trace are parsed into Steps right here and never read again — retaining
        // them verbatim would keep up to 64 KB of dead string per row alive for the store's lifetime.
        var row = new RowOutcome(
            result.DisplayName, result.Outcome, result.DurationMs, result.ErrorMessage, ErrorStackTrace: null,
            Stdout: null, result.StdoutTruncated, result.RunId, when, StepTraceParser.Parse(result.Stdout));

        int revision;
        lock (_gate)
        {
            if (!_methods.TryGetValue(key, out var method))
                _methods[key] = method = new MutableMethod();
            method.Rows[row.DisplayName] = row;
            method.LastUpdatedUtc = when;
            // A result for a method that was marked running by this run: still running until the run
            // completes (other rows may follow), but a result from a *different* run supersedes.
            if (method.RunningRunId is not null && method.RunningRunId != result.RunId)
                method.RunningRunId = null;
            revision = ++_revision;
        }

        Changed?.Invoke(this, new TestOutcomesChangedEventArgs(new[] { key }, revision));
        return key;
    }

    /// <summary>
    /// Marks the given methods as running for <paramref name="runId"/> (from the logger's <c>runStart</c>
    /// test list). Previous rows are kept — the glyph shows "running" while the details still show the
    /// last-known outcome. Cleared by <see cref="CompleteRun"/>.
    /// </summary>
    public IReadOnlyCollection<TestOutcomeKey> MarkRunning(string runId, IEnumerable<TestOutcomeKey> keys)
    {
        var affected = new List<TestOutcomeKey>();
        int revision;
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (!_methods.TryGetValue(key, out var method))
                    _methods[key] = method = new MutableMethod();
                method.RunningRunId = runId;
                affected.Add(key);
            }
            if (affected.Count == 0) return affected;
            revision = ++_revision;
        }
        Changed?.Invoke(this, new TestOutcomesChangedEventArgs(affected, revision));
        return affected;
    }

    /// <summary>Clears the running mark of every method <paramref name="runId"/> had claimed, whether or not a result arrived.</summary>
    public IReadOnlyCollection<TestOutcomeKey> CompleteRun(string runId)
    {
        var affected = new List<TestOutcomeKey>();
        int revision;
        lock (_gate)
        {
            foreach (var kvp in _methods)
            {
                if (kvp.Value.RunningRunId != runId) continue;
                kvp.Value.RunningRunId = null;
                affected.Add(kvp.Key);
            }
            // Methods that were only ever "running" (no rows, no result) carry nothing worth keeping.
            foreach (var key in affected)
                if (_methods[key].Rows.Count == 0)
                    _methods.Remove(key);
            if (affected.Count == 0) return affected;
            revision = ++_revision;
        }
        Changed?.Invoke(this, new TestOutcomesChangedEventArgs(affected, revision));
        return affected;
    }

    /// <summary>Last-known aggregate for a method, or null if no run has reported it this session.</summary>
    public MethodOutcome? TryGet(string source, string typeFullName, string methodName)
        => TryGet(TestOutcomeKey.ForLookup(source, typeFullName, methodName));

    public MethodOutcome? TryGet(TestOutcomeKey key)
    {
        lock (_gate)
        {
            return _methods.TryGetValue(key, out var method) ? method.Snapshot(key) : null;
        }
    }

    /// <summary>Everything the store knows (methods with at least one row), for diagnostics and persistence.</summary>
    public IReadOnlyList<MethodOutcome> Snapshot()
    {
        lock (_gate)
        {
            return _methods.Where(kvp => kvp.Value.Rows.Count > 0).Select(kvp => kvp.Value.Snapshot(kvp.Key)).ToList();
        }
    }

    /// <summary>
    /// Merges persisted outcomes in. A live row (recorded this session) always wins over an imported one
    /// for the same method+display name, and an imported method never overwrites a newer live method.
    /// </summary>
    public void Import(IEnumerable<MethodOutcome> outcomes)
    {
        var affected = new List<TestOutcomeKey>();
        int revision;
        lock (_gate)
        {
            foreach (var outcome in outcomes)
            {
                if (outcome.Rows.Count == 0) continue;
                if (!_methods.TryGetValue(outcome.Key, out var method))
                    _methods[outcome.Key] = method = new MutableMethod();
                else if (method.LastUpdatedUtc >= outcome.LastUpdatedUtc)
                    continue;

                foreach (var row in outcome.Rows)
                    if (!method.Rows.TryGetValue(row.DisplayName, out var existing) || existing.RecordedUtc < row.RecordedUtc)
                        method.Rows[row.DisplayName] = row;
                if (method.LastUpdatedUtc < outcome.LastUpdatedUtc)
                    method.LastUpdatedUtc = outcome.LastUpdatedUtc;
                affected.Add(outcome.Key);
            }
            if (affected.Count == 0) return;
            revision = ++_revision;
        }
        Changed?.Invoke(this, new TestOutcomesChangedEventArgs(affected, revision));
    }

    /// <summary>Forgets everything (workspace close).</summary>
    public void Clear()
    {
        IReadOnlyCollection<TestOutcomeKey> keys;
        int revision;
        lock (_gate)
        {
            keys = _methods.Keys.ToList();
            _methods.Clear();
            revision = ++_revision;
        }
        if (keys.Count > 0)
            Changed?.Invoke(this, new TestOutcomesChangedEventArgs(keys, revision));
    }

    internal static TestOutcomeKind Aggregate(IEnumerable<RowOutcome> rows)
    {
        var any = false;
        var failed = false; var passed = false; var skipped = false; var notFound = false;
        foreach (var row in rows)
        {
            any = true;
            switch (row.Outcome)
            {
                case TestOutcomeKind.Failed: failed = true; break;
                case TestOutcomeKind.Passed: passed = true; break;
                case TestOutcomeKind.Skipped: skipped = true; break;
                case TestOutcomeKind.NotFound: notFound = true; break;
            }
        }
        if (!any) return TestOutcomeKind.None;
        if (failed) return TestOutcomeKind.Failed;
        if (passed) return TestOutcomeKind.Passed;
        if (skipped) return TestOutcomeKind.Skipped;
        return notFound ? TestOutcomeKind.NotFound : TestOutcomeKind.None;
    }

    /// <summary>Maps the logger's <c>outcome</c> string (vstest <c>TestOutcome</c> names) onto <see cref="TestOutcomeKind"/>.</summary>
    public static TestOutcomeKind ParseOutcome(string? outcome)
        => outcome is not null && Enum.TryParse<TestOutcomeKind>(outcome, ignoreCase: true, out var kind) && kind != TestOutcomeKind.Running
            ? kind
            : TestOutcomeKind.None;

    private sealed class MutableMethod
    {
        public readonly Dictionary<string, RowOutcome> Rows = new(StringComparer.Ordinal);
        public DateTime LastUpdatedUtc;
        public string? RunningRunId;

        public MethodOutcome Snapshot(TestOutcomeKey key)
        {
            var rows = Rows.Values.OrderBy(r => r.DisplayName, StringComparer.Ordinal).ToList();
            return new MethodOutcome(key, Aggregate(rows), rows, LastUpdatedUtc, RunningRunId is not null);
        }
    }
}
