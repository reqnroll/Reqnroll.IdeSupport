using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.ReSharper.UnitTestFramework;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.ReSharper.UnitTestFramework.Session;
using JetBrains.Util;
using JetBrains.Util.Logging;
using Reqnroll.IdeSupport.TestReporter.Common;
using Reqnroll.IdeSupport.TestReporter.MTP;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Sends the results of tests Rider runs itself (from the test gutter marks or the Unit Tests window) to the
/// Reqnroll LSP server, the way the VSTest logger does for the plugin's own <c>dotnet test</c> runs. Rider's
/// test runners call the test frameworks directly and ignore runsettings loggers, so this is the only way such
/// runs reach the server's outcome store, and with it the failed-step marks in the <c>.feature</c> editor.
/// <para>
/// Only tests generated from a <c>.feature</c> file are sent, after each test run finishes. The server is found
/// through the session breadcrumbs its outcome listener writes, matched by the solution directory, as the MTP
/// reporter finds it. Projects that already compile the MTP reporter in are skipped, so a result is not sent
/// twice. Every failure is logged and dropped; reporting never affects a test run.
/// </para>
/// </summary>
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
public class FeatureTestResultReporter
{
    /// <summary>Wait after a run finishes, so output that arrives with the last result is included.</summary>
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(500);

    private static readonly ILogger Log = Logger.GetLogger<FeatureTestResultReporter>();

    private readonly Lifetime _lifetime;
    private readonly ISolution _solution;
    private readonly IUnitTestResultManager _results;
    private readonly object _gate = new object();
    private readonly HashSet<IUnitTestSession> _runningSessions = new HashSet<IUnitTestSession>();
    private readonly HashSet<IUnitTestSession> _endedSessions = new HashSet<IUnitTestSession>();
    private readonly Dictionary<IUnitTestElement, IUnitTestSession> _finished = new Dictionary<IUnitTestElement, IUnitTestSession>();
    private readonly Timer _flushTimer;

    public FeatureTestResultReporter(Lifetime lifetime, ISolution solution, IUnitTestResultManager results)
    {
        _lifetime = lifetime;
        _solution = solution;
        _results = results;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        lifetime.OnTermination(() => _flushTimer.Dispose());

        UT.Events.TestRun.Started.Subscribe(lifetime, OnRunStarted);
        UT.Events.TestRun.Finished.Subscribe(lifetime, OnRunFinished);
        UT.Events.Result.Updated.Subscribe(lifetime, OnResultUpdated);
    }

    private void OnRunStarted(ITestRunEvent e)
    {
        lock (_gate)
        {
            _runningSessions.Add(e.Session);
            _endedSessions.Remove(e.Session);
        }
    }

    /// <summary>
    /// Sends the run's results shortly after it finishes. The session keeps accepting results until then, so a
    /// result that lands just after the run's end is not lost.
    /// </summary>
    private void OnRunFinished(ITestRunEvent e)
    {
        lock (_gate)
            _endedSessions.Add(e.Session);
        ScheduleFlush();
    }

    /// <summary>
    /// Notes a test that just finished in a running session. Results restored from Rider's cache, or copied
    /// between sessions, arrive outside a run and are ignored: the server already has, or never had, them.
    /// </summary>
    private void OnResultUpdated(UnitTestResultEventArgs e)
    {
        if (e.Result.Outdated || RiderTestRunReport.OutcomeName(e.Result.Status.ToString()) == null)
            return;
        if (!(e.Element is ClrUnitTestElement))
            return;

        lock (_gate)
        {
            if (!_runningSessions.Contains(e.Session))
                return;
            _finished[e.Element] = e.Session;
        }
    }

    private void ScheduleFlush()
    {
        try
        {
            _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The solution is closing.
        }
    }

    /// <summary>
    /// Runs on a pool thread. The project model is read under a read lock held only for each test's identity;
    /// results and output are read under Rider's unit-test lock alone, so a large run never holds up typing.
    /// </summary>
    private void Flush()
    {
        KeyValuePair<IUnitTestElement, IUnitTestSession>[] finished;
        lock (_gate)
        {
            foreach (var session in _endedSessions)
                _runningSessions.Remove(session);
            _endedSessions.Clear();
            if (_finished.Count == 0)
                return;
            finished = _finished.ToArray();
            _finished.Clear();
        }

        try
        {
            if (!_lifetime.IsAlive)
                return;

            var reports = new List<ReportedTestResult>();
            foreach (var pair in finished)
            {
                ReportedTestResult report;
                using (ReadLockCookie.Create())
                    report = Describe(pair.Key);
                if (report == null)
                    continue;
                using (UT.ReadLock())
                {
                    if (AddResult(report, pair.Key, pair.Value))
                        reports.Add(report);
                }
            }

            if (reports.Count > 0)
                Send(_solution.SolutionDirectory.FullPath, reports);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reporting test results to the Reqnroll server failed");
        }
    }

    /// <summary>
    /// The identity of one finished test, or null if it is not a test generated from a <c>.feature</c> file, is
    /// an aggregate (a method whose rows report for themselves), or its project already reports through MTP.
    /// </summary>
    private static ReportedTestResult Describe(IUnitTestElement element)
    {
        if (element.Children.Count > 0)
            return null;

        ClrUnitTestElement.FromMethod method;
        string displayName;
        switch (element)
        {
            case ClrUnitTestElement.Row row:
                method = row.Parent as ClrUnitTestElement.FromMethod;
                displayName = row.ShortName;
                break;
            case ClrUnitTestElement.FromMethod plain:
                method = plain;
                displayName = plain.MethodName;
                break;
            default:
                return null;
        }

        var testClass = method?.Parent;
        if (testClass?.TypeName == null || string.IsNullOrEmpty(method.MethodName))
            return null;
        if (!testClass.GetProjectFiles().Any(file => RiderTestRunReport.IsFeatureCodeBehind(file.Name)))
            return null;

        var project = element.Project;
        if (project == null || HasMtpReporterStub(project))
            return null;
        var assembly = project.GetOutputFilePath(element.TargetFrameworkId);
        if (assembly.IsEmpty)
            return null;

        return new ReportedTestResult
        {
            Source = assembly.FullPath,
            ManagedType = testClass.TypeName.FullName,
            ManagedMethod = method.MethodName,
            DisplayName = string.IsNullOrEmpty(displayName) ? method.MethodName : displayName,
        };
    }

    /// <summary>Fills in the test's result in <paramref name="session"/>; false if it no longer has a finished one.</summary>
    private bool AddResult(ReportedTestResult report, IUnitTestElement element, IUnitTestSession session)
    {
        var result = _results.GetResult(element.Id, session);
        var outcome = RiderTestRunReport.OutcomeName(result.Status.ToString());
        if (outcome == null)
            return false;

        var data = _results.GetResultData(element.Id, session);
        var exception = data.ExceptionCount > 0 ? data.GetExceptionInfo(0) : null;
        report.Outcome = outcome;
        report.DurationMs = result.Duration.TotalMilliseconds;
        report.ErrorMessage = exception?.Message ?? (outcome == "Failed" ? result.ShortMessage : null);
        report.ErrorStackTrace = exception?.StackTrace;
        report.Stdout = Output(data);
        return true;
    }

    /// <summary>The test's output, read only as far as the report keeps it.</summary>
    private static string Output(UnitTestResultData data)
    {
        if (data.OutputChunksCount == 0)
            return null;
        var output = new StringBuilder();
        for (var chunk = 0; chunk < data.OutputChunksCount && output.Length <= RiderTestRunReport.MaxStdoutLength; chunk++)
            output.Append(data.GetOutputChunk(chunk));
        return output.ToString();
    }

    /// <summary>
    /// Whether the plugin's own runs left the MTP reporter stub in the project's <c>obj</c> folder; that
    /// reporter is then built into the test assembly and sends Rider's runs too.
    /// </summary>
    private static bool HasMtpReporterStub(IProject project)
    {
        var projectFile = project.ProjectFileLocation;
        if (projectFile.IsEmpty)
            return false;
        return projectFile.Directory.Combine("obj").Combine(RiderTestRunReport.MtpReporterStubFileName(projectFile.Name)).ExistsFile;
    }

    private static void Send(string solutionDirectory, IReadOnlyCollection<ReportedTestResult> reports)
    {
        try
        {
            var endpoint = SessionBreadcrumbMatcher.FindBestMatch(
                solutionDirectory, SessionBreadcrumbMatcher.ReadAll(SessionsDirectory.Resolve()))?.Endpoint;
            if (endpoint == null)
            {
                Log.Info($"No Reqnroll server session for {solutionDirectory}; {reports.Count} test result(s) not sent");
                return;
            }

            using (var sink = new OutcomeSink())
            {
                if (!sink.TryConnect(endpoint))
                {
                    Log.Info($"Could not connect to the Reqnroll server at {endpoint}; {reports.Count} test result(s) not sent");
                    return;
                }

                int pid;
                using (var self = Process.GetCurrentProcess())
                    pid = self.Id;
                foreach (var line in RiderTestRunReport.Lines(Guid.NewGuid().ToString("N"), pid, reports))
                    sink.Write(line);
            }

            Log.Info($"Sent {reports.Count} test result(s) to the Reqnroll server at {endpoint}");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, ExceptionOrigin.OuterWorld, "Sending test results to the Reqnroll server failed");
        }
    }
}
