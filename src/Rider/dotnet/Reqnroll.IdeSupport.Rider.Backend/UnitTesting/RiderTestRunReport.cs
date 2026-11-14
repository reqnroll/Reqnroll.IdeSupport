using System;
using System.Collections.Generic;
using System.Linq;
using Reqnroll.IdeSupport.TestReporter.Common;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>One finished test as Rider reported it, in the shape the LSP server's outcome store records.</summary>
public sealed class ReportedTestResult
{
    /// <summary>The test assembly the result came from.</summary>
    public string Source { get; set; }

    /// <summary>Full name of the generated test class.</summary>
    public string ManagedType { get; set; }

    /// <summary>Name of the generated test method, without parameters.</summary>
    public string ManagedMethod { get; set; }

    /// <summary>The row's name for a data row, otherwise the method name; rows of one method are told apart by it.</summary>
    public string DisplayName { get; set; }

    /// <summary>A server outcome name (see <see cref="RiderTestRunReport.OutcomeName"/>).</summary>
    public string Outcome { get; set; }

    public double DurationMs { get; set; }

    public string ErrorMessage { get; set; }

    public string ErrorStackTrace { get; set; }

    /// <summary>The test's output, which carries Reqnroll's step trace.</summary>
    public string Stdout { get; set; }
}

/// <summary>
/// Turns Rider's own test results into the newline-delimited JSON the LSP server's outcome listener reads from
/// the VSTest logger and the MTP reporter, so a run started from Rider's test gutter marks or Unit Tests window
/// updates the same outcome store. Free of Rider SDK types so it can be unit-tested.
/// </summary>
public static class RiderTestRunReport
{
    /// <summary>Wire protocol version the listener expects.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The <c>reporterKind</c> this reporter announces in its <c>hello</c>.</summary>
    public const string ReporterKind = "RiderTestRunner";

    /// <summary>Output beyond this many characters is cut, as the VSTest logger does.</summary>
    public const int MaxStdoutLength = 64 * 1024;

    private const string CodeBehindSuffix = ".feature.cs";

    /// <summary>
    /// The server outcome name for a Rider <c>UnitTestStatus</c> name, or null for a status that is not a
    /// finished result worth recording (running, pending, aborted, inconclusive, unknown).
    /// </summary>
    public static string OutcomeName(string status)
    {
        switch (status)
        {
            case "Success":
                return "Passed";
            case "Failed":
                return "Failed";
            case "Ignored":
                return "Skipped";
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="fileName"/> is the code-behind Reqnroll generates for a <c>.feature</c> file.</summary>
    public static bool IsFeatureCodeBehind(string fileName) =>
        fileName != null && fileName.EndsWith(CodeBehindSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Name of the stub the plugin writes into a project's <c>obj</c> folder to build the MTP reporter in, for
    /// the project file named <paramref name="projectFileName"/> (matches the frontend's <c>MtpProjectStubs</c>).
    /// </summary>
    public static string MtpReporterStubFileName(string projectFileName) => projectFileName + ".reqnroll-ide.targets";

    /// <summary>Cuts <paramref name="stdout"/> to <see cref="MaxStdoutLength"/>; reports whether it was cut.</summary>
    public static (string Text, bool Truncated) LimitStdout(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return (null, false);
        return stdout.Length <= MaxStdoutLength ? (stdout, false) : (stdout.Substring(0, MaxStdoutLength), true);
    }

    /// <summary>
    /// The lines of one report: a <c>hello</c>, one <c>result</c> per entry, and a <c>runComplete</c>. Each line
    /// ends with a newline. No <c>runStart</c>: the results arrive after the tests finished, so there is nothing
    /// to show as running.
    /// </summary>
    public static IReadOnlyList<string> Lines(string runId, int runnerPid, IReadOnlyCollection<ReportedTestResult> results)
    {
        if (results == null)
            throw new ArgumentNullException(nameof(results));

        var lines = new List<string>(results.Count + 2)
        {
            NdjsonWriter.Object("hello")
                .Field("protocol", ProtocolVersion)
                .Field("runId", runId)
                .Field("runnerPid", runnerPid)
                .Field("idePid", string.Empty)
                .Field("connected", true)
                .Field("reporterKind", ReporterKind)
                .ToLine(),
        };

        foreach (var result in results)
        {
            var (stdout, truncated) = LimitStdout(result.Stdout);
            lines.Add(NdjsonWriter.Object("result")
                .Field("runId", runId)
                .Field("source", result.Source)
                .Field("managedType", result.ManagedType)
                .Field("managedMethod", result.ManagedMethod)
                .Field("fqn", result.ManagedType + "." + result.ManagedMethod)
                .Field("displayName", result.DisplayName)
                .Field("outcome", result.Outcome)
                .Field("durationMs", result.DurationMs)
                .Field("errorMessage", result.ErrorMessage)
                .Field("errorStackTrace", result.ErrorStackTrace)
                .Field("stdout", stdout)
                .Field("stdoutTruncated", truncated)
                .ToLine());
        }

        lines.Add(NdjsonWriter.Object("runComplete")
            .Field("runId", runId)
            .Field("executed", results.Count)
            .Field("aborted", false)
            .Field("canceled", false)
            .Field("elapsedMs", results.Sum(r => r.DurationMs))
            .ToLine());
        return lines;
    }
}
