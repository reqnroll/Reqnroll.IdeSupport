using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.TestOutcomes;

namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>Pass/fail state of one test case (one Scenario, or one example row of an Outline).</summary>
public enum TestOutcomeKind
{
    None,
    Passed,
    Failed,
    Skipped,
    NotFound,
    /// <summary>Listed in a run's <c>runStart</c> but no result yet (Phase 3 — reserved).</summary>
    Running,
}

/// <summary>
/// Identity of a generated test method as both sides see it: the container the result came from
/// (<c>TestCase.Source</c> / <c>ScenarioTestTarget.OutputAssemblyPath</c>), the declaring type, and the
/// method name <em>without</em> its parameter signature — <c>ManagedMethod</c> carries one
/// (<c>So23(System.String,…)</c>), <c>TestMethodIdentifier.MethodName</c> doesn't, and Reqnroll never
/// generates overloaded scenario methods in one feature class, so the name alone is unambiguous.
/// </summary>
public sealed record TestOutcomeKey(string Source, string TypeFullName, string MethodName)
{
    /// <summary>
    /// Builds a key from a result's identity fields, preferring vstest's row-invariant
    /// <c>ManagedType</c>/<c>ManagedMethod</c> properties and falling back to splitting the FQN for
    /// adapters that don't set them. The source path is normalized once here (issue #515 lesson:
    /// every path comparison in the extension must go through the same routine).
    /// </summary>
    public static TestOutcomeKey? From(string? source, string? managedType, string? managedMethod, string? fullyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        string? type = managedType, method = managedMethod;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(method))
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedName)) return null;
            // NUnit bakes TestCase arguments into row FQNs — strip anything from the first '(' on
            // before locating the type/method split.
            var fqn = StripSignature(fullyQualifiedName!);
            var dot = fqn.LastIndexOf('.');
            if (dot <= 0 || dot == fqn.Length - 1) return null;
            type = fqn.Substring(0, dot);
            method = fqn.Substring(dot + 1);
        }

        return new TestOutcomeKey(PathUtils.NormalizeForComparison(source!), type!, StripSignature(method!));
    }

    /// <summary>Key for a lookup from the CodeLens side (already split identity, unnormalized path).</summary>
    public static TestOutcomeKey ForLookup(string source, string typeFullName, string methodName)
        => new(PathUtils.NormalizeForComparison(source), typeFullName, StripSignature(methodName));

    private static string StripSignature(string name)
    {
        var paren = name.IndexOf('(');
        return paren < 0 ? name : name.Substring(0, paren);
    }

    /// <summary>
    /// Source paths compare case-insensitively (Windows file system; <see cref="PathUtils.NormalizeForComparison"/>
    /// deliberately doesn't fold case), type and method names compare ordinally.
    /// </summary>
    public static IEqualityComparer<TestOutcomeKey> Comparer { get; } = new KeyComparer();

    private sealed class KeyComparer : IEqualityComparer<TestOutcomeKey>
    {
        public bool Equals(TestOutcomeKey? x, TestOutcomeKey? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(x.TypeFullName, y.TypeFullName, StringComparison.Ordinal)
                   && string.Equals(x.MethodName, y.MethodName, StringComparison.Ordinal);
        }

        public int GetHashCode(TestOutcomeKey obj)
        {
            unchecked
            {
                var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(obj.TypeFullName);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(obj.MethodName);
                return hash;
            }
        }
    }
}

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

/// <summary>Last-known state of one row (test case) of a method.</summary>
public sealed record RowOutcome(
    string DisplayName,
    TestOutcomeKind Outcome,
    double DurationMs,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? Stdout,
    bool StdoutTruncated,
    string RunId,
    DateTime RecordedUtc,
    /// <summary>Reqnroll's step trace parsed out of <see cref="Stdout"/> (execution order); empty when the output carried none.</summary>
    IReadOnlyList<StepTraceEntry> Steps)
{
    /// <summary>The first step that failed (error / binding error / undefined), or null.</summary>
    public StepTraceEntry? FailedStep => Steps.FirstOrDefault(s => s.IsFailure);
}

/// <summary>Aggregate + rows for one generated test method.</summary>
public sealed record MethodOutcome(
    TestOutcomeKey Key,
    TestOutcomeKind Aggregate,
    IReadOnlyList<RowOutcome> Rows,
    DateTime LastUpdatedUtc)
{
    public int FailedRowCount => Rows.Count(r => r.Outcome == TestOutcomeKind.Failed);
}

/// <summary>Raised after the store changes; <see cref="Keys"/> lists the affected methods.</summary>
public sealed class TestOutcomesChangedEventArgs : EventArgs
{
    public TestOutcomesChangedEventArgs(IReadOnlyCollection<TestOutcomeKey> keys, int revision)
    {
        Keys = keys;
        Revision = revision;
    }

    public IReadOnlyCollection<TestOutcomeKey> Keys { get; }
    public int Revision { get; }
}

/// <summary>
/// In-proc (devenv.exe) aggregate of test outcomes received from the bundled VSTest logger, keyed by
/// generated test method. Replaces the read side of <c>RunTestOutcomeBridge</c> (reflection into VS's
/// <c>TestStore</c>) as the Run CodeLens's outcome source; the bridge remains a fallback for projects the
/// logger never sees (implementation plan §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Aggregation rule per method: <c>Failed</c> if any row failed; else <c>Passed</c> if any row passed;
/// else <c>Skipped</c> if any row skipped; else whatever the rows say (<c>NotFound</c>/<c>None</c>).
/// Rows are upserted by display name, so a row-filtered run (one example of an outline) refreshes that
/// row without erasing the other rows' last-known state.
/// </para>
/// <para>
/// Pure and synchronous; thread-safe via one lock. No VS types, no MEF imports — the
/// <c>[Export]</c> only makes the single shared instance reachable from <see cref="TestOutcomeListener"/>
/// (writer) and <c>RunTestCodeLensCallbackListener</c> (reader).
/// </para>
/// </remarks>
[Export]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class TestOutcomeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<TestOutcomeKey, MutableMethod> _methods = new(TestOutcomeKey.Comparer);
    private int _revision;

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
        var row = new RowOutcome(
            result.DisplayName, result.Outcome, result.DurationMs, result.ErrorMessage, result.ErrorStackTrace,
            result.Stdout, result.StdoutTruncated, result.RunId, when, StepTraceParser.Parse(result.Stdout));

        int revision;
        lock (_gate)
        {
            if (!_methods.TryGetValue(key, out var method))
                _methods[key] = method = new MutableMethod();
            method.Rows[row.DisplayName] = row;
            method.LastUpdatedUtc = when;
            revision = ++_revision;
        }

        Changed?.Invoke(this, new TestOutcomesChangedEventArgs(new[] { key }, revision));
        return key;
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

    /// <summary>Everything the store knows, for diagnostics and (Phase 3) persistence.</summary>
    public IReadOnlyList<MethodOutcome> Snapshot()
    {
        lock (_gate)
        {
            return _methods.Select(kvp => kvp.Value.Snapshot(kvp.Key)).ToList();
        }
    }

    /// <summary>Forgets everything (solution close).</summary>
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

        public MethodOutcome Snapshot(TestOutcomeKey key)
        {
            var rows = Rows.Values.OrderBy(r => r.DisplayName, StringComparer.Ordinal).ToList();
            return new MethodOutcome(key, Aggregate(rows), rows, LastUpdatedUtc);
        }
    }
}
