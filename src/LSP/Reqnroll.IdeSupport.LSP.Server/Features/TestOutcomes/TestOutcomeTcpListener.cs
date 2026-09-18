#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>What <see cref="RegisterTestRunHandler"/> hands back for one run.</summary>
public sealed record TestRunRegistration(string RunId, string Endpoint, string Token);

/// <summary>
/// Server-side receiving end of the bundled VSTest logger: one TCP loopback listener per server
/// process, one connection per test run, newline-delimited JSON in, nothing out. Feeds
/// <see cref="TestOutcomeStore"/> and, debounced, pushes <c>reqnroll/testOutcomes/changed</c> to the
/// connected client so it re-pulls fresh outcomes.
/// </summary>
/// <remarks>
/// <para>
/// This is the LSP-server-side successor to the Visual Studio-only <c>TestOutcomeListener</c>: the same
/// receiver, store, and persistence now live once in the server, shared by every IDE, instead of VS
/// growing its own and Rider/VS Code eventually growing two more independent copies.
/// </para>
/// <para>
/// <b>Tokens.</b> <see cref="RegisterRun"/> mints a random token per registration; the logger's first
/// line (<c>hello</c>) must carry one that is unexpired and unused, else the connection is closed
/// unread. Tokens expire after <see cref="TokenLifetime"/>. This turns "any local process can connect"
/// into "any local process that has read this run's runsettings", which is the right size of lock for a
/// loopback-only, minutes-lived channel whose only power is posting outcomes.
/// </para>
/// <para>
/// <b>Lifetime.</b> Started lazily on the first registration, lives for the server process. Connection
/// handling is fully asynchronous on the thread pool. Every fault is logged and contained — a broken
/// listener degrades to "no live glyphs", exactly like the reflection bridge it supersedes.
/// </para>
/// </remarks>
public sealed class TestOutcomeTcpListener : IDisposable
{
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(250);

    private readonly TestOutcomeStore _store;
    private readonly TestOutcomePersistence? _persistence;
    private readonly IIdeSupportLogger _logger;
    private readonly Action _notifyChanged;
    private readonly ConcurrentDictionary<string, (string RunId, DateTime IssuedUtc)> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TcpListener? _listener;
    private Timer? _refreshTimer;
    private int _refreshScheduled;
    private bool _disposed;

    /// <summary>DI entry point: pushes <c>reqnroll/testOutcomes/changed</c> to the connected client on change.</summary>
    public TestOutcomeTcpListener(TestOutcomeStore store, TestOutcomePersistence persistence, ILanguageServerFacade languageServer, IIdeSupportLogger logger)
        : this(store, logger, () => TestOutcomesChangedRequester.NotifyChanged(languageServer, logger), persistence)
    {
    }

    /// <summary>Test seam: <paramref name="notifyChanged"/> replaces the LSP push; <paramref name="persistence"/> may be null.</summary>
    internal TestOutcomeTcpListener(TestOutcomeStore store, IIdeSupportLogger logger, Action notifyChanged, TestOutcomePersistence? persistence = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifyChanged = notifyChanged ?? throw new ArgumentNullException(nameof(notifyChanged));
        _persistence = persistence;
        _store.Changed += (_, _) => ScheduleRefresh();
    }

    /// <summary>The bound loopback endpoint once started, e.g. <c>127.0.0.1:53412</c>; null before the first registration.</summary>
    public string? Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _listener?.LocalEndpoint is IPEndPoint ep ? $"{ep.Address}:{ep.Port}" : null;
            }
        }
    }

    /// <summary>
    /// Starts the listener if needed and mints a fresh token for one run. Returns null if the listener
    /// cannot be started (port exhaustion, socket policy) — the caller then injects nothing.
    /// </summary>
    public TestRunRegistration? RegisterRun()
    {
        if (!EnsureStarted())
            return null;

        PruneExpiredTokens();
        var runId = Guid.NewGuid().ToString("N");
        var token = NewToken();
        _tokens[token] = (runId, DateTime.UtcNow);
        return new TestRunRegistration(runId, Endpoint!, token);
    }

    private bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_listener is not null) return true;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(backlog: 8);
                _listener = listener;
                _ = Task.Run(() => AcceptLoopAsync(listener));
                _logger.LogInfo($"{nameof(TestOutcomeTcpListener)}: listening on {Endpoint}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, $"{nameof(TestOutcomeTcpListener)}: failed to start loopback listener");
                return false;
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                if (_disposed) return;
                _logger.LogException(ex, $"{nameof(TestOutcomeTcpListener)}: accept failed");
                continue;
            }
            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        string? runId = null;
        var results = 0;
        var completed = false;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);

            var helloLine = await reader.ReadLineAsync().ConfigureAwait(false);
            if (helloLine is null) return;
            var hello = JObject.Parse(helloLine);
            if (!string.Equals(hello.Value<string>("type"), "hello", StringComparison.Ordinal))
            {
                _logger.LogWarning($"{nameof(TestOutcomeTcpListener)}: first line was not a hello; closing.");
                return;
            }

            var token = hello.Value<string>("token") ?? string.Empty;
            if (!_tokens.TryRemove(token, out var issued) || DateTime.UtcNow - issued.IssuedUtc > TokenLifetime)
            {
                _logger.LogWarning($"{nameof(TestOutcomeTcpListener)}: rejected connection with unknown or expired token.");
                return;
            }

            runId = hello.Value<string>("runId") ?? issued.RunId;
            var protocol = hello.Value<int?>("protocol") ?? 0;
            _logger.LogInfo($"{nameof(TestOutcomeTcpListener)}: run {runId} connected (protocol {protocol}, runner pid {hello.Value<string>("runnerPid")}, tfm {hello.Value<string>("targetFramework")})");
            if (protocol != 1)
                _logger.LogWarning($"{nameof(TestOutcomeTcpListener)}: logger protocol {protocol} differs from expected 1; parsing best-effort.");

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                JObject message;
                try { message = JObject.Parse(line); }
                catch (Exception ex)
                {
                    _logger.LogWarning($"{nameof(TestOutcomeTcpListener)}: unparseable line from run {runId}: {ex.Message}");
                    continue;
                }

                switch (message.Value<string>("type"))
                {
                    case "runStart":
                        var running = _store.MarkRunning(runId, ParseRunStartTests(message));
                        _logger.LogVerbose($"{nameof(TestOutcomeTcpListener)}: run {runId} started, {message.Value<int?>("testCount") ?? 0} test(s), {running.Count} method(s) marked running");
                        break;
                    case "result":
                        if (_store.Record(ToRecord(runId, message)) is not null) results++;
                        break;
                    case "runComplete":
                        completed = true;
                        _logger.LogInfo($"{nameof(TestOutcomeTcpListener)}: run {runId} complete — executed {message.Value<int?>("executed") ?? 0}, aborted={message.Value<bool?>("aborted") ?? false}, canceled={message.Value<bool?>("canceled") ?? false}, {results} result(s) stored");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogVerbose($"{nameof(TestOutcomeTcpListener)}: connection for run {runId ?? "?"} dropped: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomeTcpListener)}: connection handler failed for run {runId ?? "?"}");
        }
        finally
        {
            if (runId is not null && !completed)
                _logger.LogWarning($"{nameof(TestOutcomeTcpListener)}: run {runId} closed without runComplete after {results} result(s) — treated as aborted.");
            try { client.Dispose(); } catch (Exception) { }
            if (runId is not null)
            {
                // Whether the run completed or the runner died: nothing is running any more, and
                // whatever arrived is worth keeping for the next session.
                _store.CompleteRun(runId);
                if (results > 0)
                    _persistence?.Save(_store.Snapshot());
            }
            // Make sure the client catches up even if the debounce timer was cancelled by disposal ordering.
            if (results > 0) ScheduleRefresh();
        }
    }

    /// <summary>Mirror of the logger's <c>ReqnrollIdeTestLogger.TestIdentitySeparator</c> (U+001F).</summary>
    internal const char TestIdentitySeparator = (char)0x1f;

    /// <summary>
    /// <c>runStart.tests</c>: one packed identity per selected test case
    /// (<c>source␟managedType␟managedMethod␟fqn␟displayName</c>, U+001F-separated — see the logger's
    /// <c>FormatRunStart</c>). Absent for source-based runs.
    /// </summary>
    internal static IReadOnlyList<TestOutcomeKey> ParseRunStartTests(JObject message)
    {
        var keys = new List<TestOutcomeKey>();
        if (message["tests"] is not JArray tests) return keys;
        foreach (var entry in tests)
        {
            var packed = entry.Value<string>();
            if (string.IsNullOrEmpty(packed)) continue;
            var parts = packed!.Split(TestIdentitySeparator);
            if (parts.Length < 4) continue;
            var key = TestOutcomeKey.From(parts[0], parts[1], parts[2], parts[3]);
            if (key is not null && !keys.Contains(key, TestOutcomeKey.Comparer))
                keys.Add(key);
        }
        return keys;
    }

    internal static TestResultRecord ToRecord(string runId, JObject message) => new(
        RunId: message.Value<string>("runId") ?? runId,
        Source: message.Value<string>("source") ?? string.Empty,
        ManagedType: message.Value<string>("managedType"),
        ManagedMethod: message.Value<string>("managedMethod"),
        FullyQualifiedName: message.Value<string>("fqn") ?? string.Empty,
        DisplayName: message.Value<string>("displayName") ?? message.Value<string>("fqn") ?? string.Empty,
        Outcome: TestOutcomeStore.ParseOutcome(message.Value<string>("outcome")),
        DurationMs: message.Value<double?>("durationMs") ?? 0,
        ErrorMessage: message.Value<string>("errorMessage"),
        ErrorStackTrace: message.Value<string>("errorStackTrace"),
        Stdout: message.Value<string>("stdout"),
        StdoutTruncated: message.Value<bool?>("stdoutTruncated") ?? false);

    /// <summary>
    /// Throttle, not debounce: once a fire is scheduled, further calls during the wait are absorbed
    /// (a single <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, no <see cref="Timer.Change"/>
    /// call) rather than each resetting the wait, so a fast, continuous stream of results — e.g. a
    /// large Scenario Outline finishing in well under <see cref="RefreshDebounce"/> — fires at a
    /// bounded, predictable cadence instead of two failure modes a reset-on-every-call debounce is
    /// prone to: many redundant <c>Timer.Change</c> calls during the burst, or, if results keep
    /// arriving faster than the debounce interval for long enough, never firing at all until the
    /// stream finally goes quiet (starvation — the exact opposite of "live" outcomes). Deliberately NOT
    /// the shared <c>IRefreshDebouncer</c> used elsewhere in this server: that is a reset-on-every-call
    /// debounce by design (see its remarks), which is exactly the starvation-prone shape this throttle
    /// was built to avoid.
    /// </summary>
    /// <remarks>
    /// This does <em>not</em> bound how often a full invalidation happens when results are spaced
    /// <em>further</em> apart than <see cref="RefreshDebounce"/> (e.g. a slow-running suite) — each
    /// such result still triggers its own fire, same as before. Scoping invalidation to only the
    /// files/lines an outcome change actually affects (rather than every open <c>.feature</c> file)
    /// would need the store's changed keys mapped back to file/line, which the client side resolves
    /// lazily and deliberately never does today — left as a known follow-up.
    /// </remarks>
    private void ScheduleRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshScheduled, 1, 0) != 0)
            return; // Already queued to fire within the interval; this event will be covered by it.

        lock (_gate)
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _refreshScheduled, 0);
                return;
            }
            _refreshTimer ??= new Timer(_ => FireRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            _refreshTimer.Change(RefreshDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireRefresh()
    {
        Interlocked.Exchange(ref _refreshScheduled, 0);
        try { _notifyChanged(); }
        catch (Exception ex) { _logger.LogException(ex, $"{nameof(TestOutcomeTcpListener)}: change notification failed"); }
    }

    private void PruneExpiredTokens()
    {
        var cutoff = DateTime.UtcNow - TokenLifetime;
        foreach (var kvp in _tokens)
            if (kvp.Value.IssuedUtc < cutoff)
                _tokens.TryRemove(kvp.Key, out _);
    }

    private static string NewToken()
    {
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _listener?.Stop(); } catch (Exception) { }
            _listener = null;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }
}

/// <summary>Sends the <c>reqnroll/testOutcomes/changed</c> notification to the connected client. Mirrors <see cref="Reqnroll.IdeSupport.LSP.Server.Features.CodeLens.CodeLensRefreshRequester"/>.</summary>
internal static class TestOutcomesChangedRequester
{
    public static void NotifyChanged(ILanguageServerFacade languageServer, IIdeSupportLogger logger)
    {
        try
        {
            languageServer.SendNotification(LspMethodNames.ReqnrollTestOutcomesChanged, new TestOutcomesChangedParams());
        }
        catch (Exception ex)
        {
            logger.LogWarning($"{LspMethodNames.ReqnrollTestOutcomesChanged} failed: {ex.Message}");
        }
    }
}
