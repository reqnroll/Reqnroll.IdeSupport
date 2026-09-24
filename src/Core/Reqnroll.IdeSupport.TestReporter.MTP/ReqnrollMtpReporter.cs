using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestHost;
using Microsoft.Testing.Platform.Services;
using Reqnroll.IdeSupport.TestReporter.Common;

namespace Reqnroll.IdeSupport.TestReporter.MTP;

/// <summary>
/// MTP composite extension (<see cref="ITestSessionLifetimeHandler"/> + <see cref="IDataConsumer"/>)
/// that streams every <see cref="TestNodeUpdateMessage"/> — including one per Scenario Outline example
/// row — to the LSP server that owns this workspace, over the same NDJSON-over-TCP-loopback protocol
/// <c>Reqnroll.IdeSupport.TestLogger</c> already speaks (issue #715, MTP phase of #700/#702's pipeline).
/// </summary>
/// <remarks>
/// <para>
/// Unlike the VSTest logger — which the IDE registers per run via runsettings, handing it the
/// endpoint directly — this reporter is compiled into the test host with no per-run configuration
/// channel available (MTP has none; see the implementation plan §3). It discovers where to connect
/// itself: resolve this assembly's own workspace root (<see cref="WorkspaceRootLocator"/>), read the
/// LSP server's session breadcrumb files (<see cref="SessionBreadcrumbMatcher"/>), and connect to the
/// deepest matching one. No match, or a connection failure, leaves it inert — the same "can't reach
/// the IDE" degradation the VSTest logger already has.
/// </para>
/// <para>
/// Wire format: identical to the VSTest logger's (<see cref="NdjsonWriter"/>/<see cref="OutcomeSink"/>,
/// shared via <c>Reqnroll.IdeSupport.TestReporter.Common</c>) — <c>hello</c>, <c>result</c> (one per
/// <see cref="TestNodeUpdateMessage"/> carrying a terminal state), <c>runComplete</c>. There is no
/// <c>runStart</c>: MTP does not hand a reporter the full test list up front the way vstest's
/// <c>TestRunStart</c> event does, so "running" marks simply never appear for MTP-sourced runs — final
/// results still do.
/// </para>
/// </remarks>
internal sealed class ReqnrollMtpReporter : ITestSessionLifetimeHandler, IDataConsumer
{
    internal const int ProtocolVersion = 1;
    internal const int MaxStdoutLength = 64 * 1024;

    private readonly OutcomeSink _sink = new();
    private readonly Func<string?> _findEndpoint;
    private bool _endpointResolved;
    private string? _endpoint;
    private string _runId = string.Empty;
    private string _source = string.Empty;
    private int _results;

    public ReqnrollMtpReporter() : this(FindSessionEndpoint) { }

    /// <param name="findEndpoint">Breadcrumb discovery; injected for tests. Production uses <see cref="FindSessionEndpoint"/>.</param>
    internal ReqnrollMtpReporter(Func<string?> findEndpoint) => _findEndpoint = findEndpoint;

    // ---- IExtension ----
    public string Uid => "Reqnroll.IdeSupport.TestReporter.MTP";
    public string Version => "1.0.0";
    public string DisplayName => "Reqnroll IDE Support (MTP)";
    public string Description => "Streams Microsoft.Testing.Platform test outcomes to the Reqnroll IDE Support LSP server.";

    /// <summary>
    /// Issue #741 §2.4a: enabled only when an IDE session breadcrumb matches this test project's
    /// workspace. With no IDE session there is nothing to report to, so MTP never activates this
    /// extension at all (no data-consumer subscription, no session hooks) — the same "disable yourself
    /// in IsEnabledAsync" pattern Tyrrrz/GitHubActionsTestLogger uses. Only the breadcrumb lookup
    /// happens here; the TCP connect stays in <see cref="OnTestSessionStartingAsync"/>.
    /// </summary>
    public Task<bool> IsEnabledAsync() => Task.FromResult(ResolveEndpoint() is not null);

    // ---- IDataConsumer ----
    public Type[] DataTypesConsumed { get; } = [typeof(TestNodeUpdateMessage)];

    public Task ConsumeAsync(IDataProducer dataProducer, IData value, CancellationToken cancellationToken)
    {
        if (_sink.IsActive && value is TestNodeUpdateMessage message)
        {
            var line = FormatResult(message);
            if (line is not null) _sink.Write(line);
        }
        return Task.CompletedTask;
    }

    // ---- ITestSessionLifetimeHandler ----
    public Task OnTestSessionStartingAsync(ITestSessionContext context)
    {
        _runId = context.SessionUid.Value;
        _source = Path.GetFullPath(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty);

        var connected = TryConnect();
        if (!_sink.IsActive)
            return Task.CompletedTask; // Nowhere to send: stay inert.

        int runnerPid;
        using (var self = Process.GetCurrentProcess()) runnerPid = self.Id;

        _sink.Write(NdjsonWriter.Object("hello")
            .Field("protocol", ProtocolVersion)
            .Field("runId", _runId)
            .Field("runnerPid", runnerPid)
            .Field("connected", connected)
            .ToLine());

        return Task.CompletedTask;
    }

    public Task OnTestSessionFinishingAsync(ITestSessionContext context)
    {
        if (_sink.IsActive)
            _sink.Write(FormatRunComplete(context.CancellationToken.IsCancellationRequested));
        _sink.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <paramref name="canceled"/> comes from <see cref="ITestSessionContext.CancellationToken"/>, which
    /// is signaled for a user-initiated cancel (IDE Cancel button, Ctrl+C, <c>--test-timeout</c>) that
    /// still reaches this orderly-teardown hook — a real, observable signal, so it must be read rather
    /// than hardcoded. <c>aborted</c> stays hardcoded <c>false</c>: a genuine abort (host process
    /// crashed/killed) means <see cref="OnTestSessionFinishingAsync"/> never runs at all — MTP only calls
    /// it once the framework "has finished executing all tests and has reported all relevant data to the
    /// platform". The server already infers an abort from the connection dropping without a runComplete
    /// line (see <c>TestOutcomeTcpListener.HandleConnectionAsync</c>'s finally block), so there is no
    /// case where this reporter could observe an abort and reach this line to report it.
    /// </summary>
    internal string FormatRunComplete(bool canceled) =>
        NdjsonWriter.Object("runComplete")
            .Field("runId", _runId)
            .Field("executed", _results)
            .Field("aborted", false)
            .Field("canceled", canceled)
            .ToLine();

    /// <summary>Connect-and-verify to the endpoint <see cref="IsEnabledAsync"/> found. Every failure is swallowed: reporting must never fail the test run.</summary>
    private bool TryConnect()
    {
        try
        {
            var endpoint = ResolveEndpoint();
            return endpoint is not null && _sink.TryConnect(endpoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Looks the endpoint up once per reporter instance — MTP asks <see cref="IsEnabledAsync"/> before the session starts, and the session must connect to the same endpoint it was enabled for.</summary>
    private string? ResolveEndpoint()
    {
        if (!_endpointResolved)
        {
            _endpoint = _findEndpoint();
            _endpointResolved = true;
        }
        return _endpoint;
    }

    /// <summary>
    /// Breadcrumb discovery: resolve this assembly's own workspace root, read the LSP servers' session
    /// breadcrumbs and pick the deepest match. When the reporter is compiled into the user's test
    /// assembly (issue #741), "this assembly" is that test assembly, so the lookup starts from the test
    /// project's own output directory. Every failure is swallowed and means "no session".
    /// </summary>
    internal static string? FindSessionEndpoint()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(ReqnrollMtpReporter).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDir))
                return null;

            var myWorkspaceRoot = WorkspaceRootLocator.FindNearestRoot(assemblyDir, WorkspaceRootLocator.LooksLikeRoot);
            var candidates = SessionBreadcrumbMatcher.ReadAll(SessionsDirectory.Resolve());
            return SessionBreadcrumbMatcher.FindBestMatch(myWorkspaceRoot, candidates)?.Endpoint;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? FormatResult(TestNodeUpdateMessage message)
    {
        var testNode = message.TestNode;
        var (outcome, errorMessage, errorStackTrace) = ResolveOutcome(testNode);
        if (outcome is null)
            return null; // Non-terminal update (discovered/in-progress): nothing to report yet.
        _results++;

        var identifier = testNode.Properties.SingleOrDefault<TestMethodIdentifierProperty>();
        var managedType = identifier is null ? null : $"{identifier.Namespace}.{identifier.TypeName}";
        var managedMethod = identifier is null ? null : $"{identifier.MethodName}({string.Join(",", identifier.ParameterTypeFullNames)})";
        var fqn = identifier is null ? testNode.Uid.Value : $"{managedType}.{identifier.MethodName}";
        var durationMs = testNode.Properties.SingleOrDefault<TimingProperty>()?.GlobalTiming.Duration.TotalMilliseconds ?? 0;
        var (stdout, truncated) = CollectStandardOutput(testNode);

        return NdjsonWriter.Object("result")
            .Field("runId", _runId)
            .Field("source", _source)
            .Field("managedType", managedType)
            .Field("managedMethod", managedMethod)
            .Field("fqn", fqn)
            .Field("displayName", testNode.DisplayName)
            .Field("outcome", outcome)
            .Field("durationMs", durationMs)
            .Field("errorMessage", errorMessage)
            .Field("errorStackTrace", errorStackTrace)
            .Field("stdout", stdout)
            .Field("stdoutTruncated", truncated)
            .ToLine();
    }

    /// <summary>
    /// One and only one <see cref="TestNodeStateProperty"/>-derived property is set per terminal
    /// <see cref="TestNodeUpdateMessage"/>. Maps onto the same outcome vocabulary
    /// <c>TrxUnitTestResult.Outcome</c> already uses (issue #715 plan §4): Error/Timeout both collapse
    /// to "Failed" — this codebase's IDE-facing <c>TestOutcomeKind</c> has no separate states for them,
    /// same as the VSTest logger's own outcome mapping. <c>CancelledTestNodeStateProperty</c> is
    /// obsolete (frameworks now signal cancellation via <see cref="OperationCanceledException"/>), but
    /// frameworks that still emit it would otherwise get no outcome at all, so it is mapped to
    /// "Skipped" inside a narrow CS0618 suppression (issue #741 §2.2 — a pragma removes the warning
    /// before TreatWarningsAsErrors/WarningsAsErrors can promote it, including in a user's project the
    /// sources are compiled into); in-progress/discovered states and any other unrecognized state
    /// return a null outcome so the caller skips sending a "result" for a non-terminal update.
    /// </summary>
    internal static (string? Outcome, string? ErrorMessage, string? ErrorStackTrace) ResolveOutcome(TestNode testNode)
    {
        var state = testNode.Properties.SingleOrDefault<TestNodeStateProperty>();
        return state switch
        {
            PassedTestNodeStateProperty => ("Passed", null, null),
            SkippedTestNodeStateProperty => ("Skipped", null, null),
            FailedTestNodeStateProperty failed => ("Failed", failed.Exception?.Message ?? failed.Explanation, failed.Exception?.StackTrace),
            ErrorTestNodeStateProperty error => ("Failed", error.Exception?.Message ?? error.Explanation, error.Exception?.StackTrace),
            TimeoutTestNodeStateProperty timeout => ("Failed", timeout.Exception?.Message ?? timeout.Explanation, timeout.Exception?.StackTrace),
#pragma warning disable CS0618 // CancelledTestNodeStateProperty is obsolete; still emitted by some frameworks.
            CancelledTestNodeStateProperty => ("Skipped", null, null),
#pragma warning restore CS0618
            _ => (null, null, null),
        };
    }

    private static (string? Text, bool Truncated) CollectStandardOutput(TestNode testNode)
    {
        // StandardOutputProperty is [Experimental("TPEXP")] in Microsoft.Testing.Platform 2.0.x, which
        // reports as an error unless suppressed, both here and in any project the injected sources
        // are compiled into.
#pragma warning disable TPEXP
        var output = testNode.Properties.SingleOrDefault<StandardOutputProperty>()?.StandardOutput;
#pragma warning restore TPEXP
        if (string.IsNullOrEmpty(output)) return (null, false);
        if (output.Length <= MaxStdoutLength) return (output, false);
        return (output.Substring(0, MaxStdoutLength), true);
    }
}
