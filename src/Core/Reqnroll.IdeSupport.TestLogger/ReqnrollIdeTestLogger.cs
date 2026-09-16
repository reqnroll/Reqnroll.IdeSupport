using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

namespace Reqnroll.IdeSupport.TestLogger;

/// <summary>
/// VSTest logger that records every individual <see cref="TestResult"/> of an IDE-triggered run —
/// including one per Scenario Outline example row — so the IDE can derive pass/fail state from the
/// standard vstest pipeline instead of VS's internal, outline-blind test-outcome push (issue #702).
/// </summary>
/// <remarks>
/// <para>
/// Registered by the IDE side (VS: the <c>IRunSettingsService</c> export in VSSDKIntegration) through
/// the runsettings it injects into each run:
/// <c>&lt;RunConfiguration&gt;&lt;TestAdaptersPaths&gt;</c> pointing at the extension's bundled copy of this
/// assembly, and <c>&lt;LoggerRunSettings&gt;&lt;Loggers&gt;&lt;Logger friendlyName="ReqnrollIde"&gt;</c>
/// whose <c>&lt;Configuration&gt;</c> children arrive here as the parameter dictionary of
/// <see cref="Initialize(TestLoggerEvents, Dictionary{string, string})"/>.
/// </para>
/// <para>
/// <b>Spike scope:</b> this writes a line-oriented log file (path supplied by the IDE in
/// <see cref="LogFilePathParameter"/>) rather than talking to the IDE over IPC — the feasibility
/// question it answers is "does the IDE's injected registration reach us with our parameters
/// intact?", not the wire format. The IPC channel is the next design step; the parameter that will
/// identify its endpoint is already carried (<see cref="IdeProcessIdParameter"/>).
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

    /// <summary><c>&lt;Configuration&gt;</c> element: absolute path of the file this logger appends to.</summary>
    public const string LogFilePathParameter = "LogFilePath";

    /// <summary><c>&lt;Configuration&gt;</c> element: PID of the IDE instance that injected the registration.</summary>
    public const string IdeProcessIdParameter = "IdeProcessId";

    // vstest's ManagedNameConstants (Microsoft.TestPlatform.AdapterUtilities) — the row-invariant
    // identity every adapter attaches to a test case. Read by id off TestCase.Properties rather than
    // via TestProperty.Find so this assembly needs no AdapterUtilities reference.
    private const string ManagedTypePropertyId = "TestCase.ManagedType";
    private const string ManagedMethodPropertyId = "TestCase.ManagedMethod";

    private readonly object _writeLock = new();
    private string? _logFilePath;
    private string _ideProcessId = "?";

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

        _logFilePath = ResolveLogFilePath(parameters);
        // netstandard2.0's string.IsNullOrWhiteSpace carries no nullability annotation, hence the
        // explicit null checks alongside it here and below.
        if (parameters.TryGetValue(IdeProcessIdParameter, out var pid) && pid is not null && !string.IsNullOrWhiteSpace(pid))
            _ideProcessId = pid;

        Append($"initialize runner-pid={Process.GetCurrentProcess().Id} ide-pid={_ideProcessId} parameters={FormatParameters(parameters)}");

        events.TestRunStart += (_, e) => Append($"run-start sources={e.TestRunCriteria.Sources?.Count() ?? 0} tests={e.TestRunCriteria.Tests?.Count() ?? 0}");
        events.TestResult += (_, e) => Append(FormatResult(e.Result));
        events.TestRunComplete += (_, e) => Append($"run-complete executed={e.TestRunStatistics?.ExecutedTests ?? 0} aborted={e.IsAborted} canceled={e.IsCanceled} elapsed={e.ElapsedTimeInRunningTests}");
    }

    private static string ResolveLogFilePath(Dictionary<string, string?> parameters)
    {
        if (parameters.TryGetValue(LogFilePathParameter, out var configured) && configured is not null && !string.IsNullOrWhiteSpace(configured))
            return configured;

        // Fallback for a registration that carried no path (hand-written runsettings, or the
        // parameterless Initialize overload): the run's own results directory, so the file is at
        // least discoverable next to the TRX/deployment output.
        var dir = parameters.TryGetValue(DefaultLoggerParameterNames.TestRunDirectory, out var runDir) && runDir is not null && !string.IsNullOrWhiteSpace(runDir)
            ? runDir
            : Path.GetTempPath();
        return Path.Combine(dir, "reqnroll-testlogger.log");
    }

    private static string FormatResult(TestResult result)
    {
        var tc = result.TestCase;
        return $"result outcome={result.Outcome} fqn={tc.FullyQualifiedName} display=\"{result.DisplayName ?? tc.DisplayName}\" " +
               $"managedType={GetProperty(tc, ManagedTypePropertyId)} managedMethod={GetProperty(tc, ManagedMethodPropertyId)} " +
               $"source={tc.Source} duration={result.Duration}";
    }

    private static string GetProperty(TestCase testCase, string id)
    {
        foreach (var property in testCase.Properties)
        {
            if (string.Equals(property.Id, id, StringComparison.Ordinal))
                return testCase.GetPropertyValue(property)?.ToString() ?? "(null)";
        }
        return "(absent)";
    }

    private static string FormatParameters(Dictionary<string, string?> parameters)
    {
        var sb = new StringBuilder("{");
        foreach (var kvp in parameters)
        {
            if (sb.Length > 1) sb.Append(", ");
            sb.Append(kvp.Key).Append('=').Append(kvp.Value);
        }
        return sb.Append('}').ToString();
    }

    private void Append(string message)
    {
        var path = _logFilePath;
        if (path is null) return;
        var line = DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
        lock (_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
            }
            catch (Exception)
            {
                // A logger must never fail the test run. There is nothing sensible to do with the
                // error inside the runner process; the IDE side will notice the missing file.
            }
        }
    }
}
