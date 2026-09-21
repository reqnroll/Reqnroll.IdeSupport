using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Reqnroll.IdeSupport.TestReporter.Common;

namespace Reqnroll.IdeSupport.TestLogger;

/// <summary>
/// VSTest logger that streams every individual <see cref="TestResult"/> of an IDE-triggered run —
/// including one per Scenario Outline example row — to the IDE that registered it, so the IDE can
/// derive pass/fail state from the standard vstest pipeline instead of VS's internal, outline-blind
/// test-outcome push (issue #702).
/// </summary>
/// <remarks>
/// <para>
/// Registered by the IDE side (VS: the <c>IRunSettingsService</c> export in VSSDKIntegration) through
/// the runsettings it injects into each run: <c>&lt;RunConfiguration&gt;&lt;TestAdaptersPaths&gt;</c> pointing
/// at the extension's bundled copy of this assembly, and
/// <c>&lt;LoggerRunSettings&gt;&lt;Loggers&gt;&lt;Logger friendlyName="ReqnrollIde"&gt;</c> whose
/// <c>&lt;Configuration&gt;</c> children arrive here as the parameter dictionary. Rider / VS Code pass the
/// same values as <c>--logger "ReqnrollIde;Endpoint=…"</c>.
/// </para>
/// <para>
/// Wire format: newline-delimited JSON over one TCP loopback connection, IDE sends nothing back.
/// Messages: <c>hello</c>, <c>runStart</c>, <c>result</c> (one per test case), <c>runComplete</c>.
/// Field names are the contract with <c>TestOutcomeListener</c> on the IDE side; there is no
/// per-connection secret — the loopback bind is the whole trust boundary (see
/// <c>TestOutcomeTcpListener</c>'s remarks for why an earlier per-run token was removed).
/// <see cref="ProtocolVersion"/> is bumped for incompatible changes, unknown fields are ignored.
/// </para>
/// <para>
/// Without an <c>Endpoint</c> the logger is inert (a hand-written runsettings naming it must not break
/// a run). Every sink failure ends in "stop sending" — never in an exception reaching vstest.
/// </para>
/// </remarks>
[FriendlyName(FriendlyName)]
[ExtensionUri(ExtensionUri)]
public sealed class ReqnrollIdeTestLogger : ITestLoggerWithParameters
{
    /// <summary>The <c>friendlyName</c> the IDE puts on the injected <c>&lt;Logger&gt;</c> element.</summary>
    public const string FriendlyName = "ReqnrollIde";

    /// <summary>Stable extension URI; the IDE may register by URI instead of friendly name.</summary>
    public const string ExtensionUri = "logger://Reqnroll/IdeSupport/v1";

    /// <summary>Bumped for incompatible wire-format changes; sent in <c>hello</c>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary><c>host:port</c> the IDE listens on for this run (loopback).</summary>
    public const string EndpointParameter = "Endpoint";

    /// <summary>Correlation id echoed in every message.</summary>
    public const string RunIdParameter = "RunId";

    /// <summary>PID of the IDE instance that injected the registration (diagnostic only).</summary>
    public const string IdeProcessIdParameter = "IdeProcessId";

    /// <summary>Optional troubleshooting mirror: absolute path of a file the same NDJSON lines are appended to.</summary>
    public const string LogFilePathParameter = "LogFilePath";

    /// <summary>Environment variable equivalent of <see cref="LogFilePathParameter"/> (set on the runner process).</summary>
    public const string LogFileEnvironmentVariable = "REQNROLL_TESTLOGGER_FILE";

    /// <summary>Upper bound on the captured stdout forwarded per result; the rest is dropped and flagged.</summary>
    public const int MaxStdoutLength = 64 * 1024;

    // vstest's ManagedNameConstants (Microsoft.TestPlatform.AdapterUtilities) — the row-invariant
    // identity every adapter attaches to a test case. Read by id off TestCase.Properties rather than
    // via TestProperty.Find so this assembly needs no AdapterUtilities reference.
    private const string ManagedTypePropertyId = "TestCase.ManagedType";
    private const string ManagedMethodPropertyId = "TestCase.ManagedMethod";

    private readonly OutcomeSink _sink = new();
    private string _runId = string.Empty;

    /// <inheritdoc />
    public void Initialize(TestLoggerEvents events, string testRunDirectory)
        => Initialize(events, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultLoggerParameterNames.TestRunDirectory] = testRunDirectory,
        });

    /// <inheritdoc />
    public void Initialize(TestLoggerEvents events, Dictionary<string, string?> parameters)
    {
        if (events is null) throw new ArgumentNullException(nameof(events));
        parameters ??= new Dictionary<string, string?>();

        _runId = Get(parameters, RunIdParameter) ?? Guid.NewGuid().ToString("N");
        var idePid = Get(parameters, IdeProcessIdParameter) ?? string.Empty;

        var filePath = Get(parameters, LogFilePathParameter) ?? Environment.GetEnvironmentVariable(LogFileEnvironmentVariable);
        if (filePath is not null && !string.IsNullOrWhiteSpace(filePath))
            _sink.SetFile(filePath);

        var endpoint = Get(parameters, EndpointParameter);
        var connected = endpoint is not null && _sink.TryConnect(endpoint);

        if (!_sink.IsActive)
            return; // Nowhere to send: stay inert, don't even subscribe.

        int runnerPid;
        using (var self = Process.GetCurrentProcess()) runnerPid = self.Id;

        _sink.Write(NdjsonWriter.Object("hello")
            .Field("protocol", ProtocolVersion)
            .Field("runId", _runId)
            .Field("runnerPid", runnerPid)
            .Field("idePid", idePid)
            .Field("connected", connected)
            .Field("targetFramework", Get(parameters, DefaultLoggerParameterNames.TargetFramework) ?? string.Empty)
            .Field("testRunDirectory", Get(parameters, DefaultLoggerParameterNames.TestRunDirectory) ?? string.Empty)
            .ToLine());

        events.TestRunStart += (_, e) => _sink.Write(FormatRunStart(e));
        events.TestResult += (_, e) => _sink.Write(FormatResult(e.Result));
        events.TestRunComplete += (_, e) =>
        {
            _sink.Write(FormatRunComplete(e));
            _sink.Dispose();
        };
    }

    /// <summary>Upper bound on the test identities listed in <c>runStart</c>; beyond it the IDE just gets the count.</summary>
    public const int MaxRunStartTests = 500;

    /// <summary>Separates the five identity fields of each <c>runStart.tests</c> entry.</summary>
    public const char TestIdentitySeparator = '';

    private string FormatRunStart(TestRunStartEventArgs e)
    {
        var criteria = e.TestRunCriteria;
        var tests = criteria?.Tests?.ToList();
        var sources = criteria?.Sources?.ToList()
                      ?? tests?.Select(t => t.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                      ?? new List<string>();

        // For a selected-tests run (Run on a lens / in Test Explorer) the runner knows the cases up
        // front; the IDE uses them to show "running" on the right lenses. A source-based run
        // (Run All) has no list — the IDE just sees the count 0 and waits for results. Each entry packs
        // the same identity fields as a result — source, managedType, managedMethod, fqn, displayName —
        // separated by U+001F (unit separator), which cannot occur in any of them, so the writer's
        // vocabulary stays flat (string arrays only).
        var identities = (tests ?? new List<TestCase>())
            .Take(MaxRunStartTests)
            .Select(t => string.Join(TestIdentitySeparator.ToString(), t.Source ?? string.Empty, GetProperty(t, ManagedTypePropertyId) ?? string.Empty,
                GetProperty(t, ManagedMethodPropertyId) ?? string.Empty, t.FullyQualifiedName ?? string.Empty, t.DisplayName ?? string.Empty));

        return NdjsonWriter.Object("runStart")
            .Field("runId", _runId)
            .Field("testCount", tests?.Count ?? 0)
            .Field("sources", sources)
            .Field("tests", identities)
            .ToLine();
    }

    private string FormatResult(TestResult result)
    {
        var tc = result.TestCase;
        var (stdout, truncated) = CollectStandardOutput(result);
        return NdjsonWriter.Object("result")
            .Field("runId", _runId)
            .Field("source", tc.Source)
            .Field("managedType", GetProperty(tc, ManagedTypePropertyId))
            .Field("managedMethod", GetProperty(tc, ManagedMethodPropertyId))
            .Field("fqn", tc.FullyQualifiedName)
            .Field("displayName", result.DisplayName ?? tc.DisplayName)
            .Field("outcome", result.Outcome.ToString())
            .Field("durationMs", result.Duration.TotalMilliseconds)
            .Field("errorMessage", result.ErrorMessage)
            .Field("errorStackTrace", result.ErrorStackTrace)
            .Field("stdout", stdout)
            .Field("stdoutTruncated", truncated)
            .ToLine();
    }

    private string FormatRunComplete(TestRunCompleteEventArgs e)
        => NdjsonWriter.Object("runComplete")
            .Field("runId", _runId)
            .Field("executed", e.TestRunStatistics?.ExecutedTests ?? 0)
            .Field("aborted", e.IsAborted)
            .Field("canceled", e.IsCanceled)
            .Field("elapsedMs", e.ElapsedTimeInRunningTests.TotalMilliseconds)
            .ToLine();

    private static (string? Text, bool Truncated) CollectStandardOutput(TestResult result)
    {
        StringBuilder? sb = null;
        foreach (var message in result.Messages)
        {
            if (!string.Equals(message.Category, TestResultMessage.StandardOutCategory, StringComparison.Ordinal))
                continue;
            if (string.IsNullOrEmpty(message.Text))
                continue;
            sb ??= new StringBuilder();
            sb.Append(message.Text);
        }

        if (sb is null) return (null, false);
        if (sb.Length <= MaxStdoutLength) return (sb.ToString(), false);
        return (sb.ToString(0, MaxStdoutLength), true);
    }

    private static string? GetProperty(TestCase testCase, string id)
    {
        foreach (var property in testCase.Properties)
        {
            if (string.Equals(property.Id, id, StringComparison.Ordinal))
                return testCase.GetPropertyValue(property)?.ToString();
        }
        return null;
    }

    // netstandard2.0's string.IsNullOrWhiteSpace carries no nullability annotation, hence the explicit null test.
    private static string? Get(Dictionary<string, string?> parameters, string key)
        => parameters.TryGetValue(key, out var value) && value is not null && !string.IsNullOrWhiteSpace(value) ? value : null;
}
