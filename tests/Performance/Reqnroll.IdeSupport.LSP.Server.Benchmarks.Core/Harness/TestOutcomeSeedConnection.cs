#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;

/// <summary>
/// One synthetic test result to post through the server's outcome listener, carrying the same
/// identity fields the bundled VSTest logger's <c>result</c> message carries (see
/// <c>ReqnrollIdeTestLogger.FormatResult</c> — <c>TestOutcomeTcpListener.ToRecord</c> is the reader on
/// the other end).
/// </summary>
/// <param name="Source">The container assembly the generated test method lives in (<c>TestCase.Source</c>).</param>
/// <param name="ManagedType">The declaring type; with <paramref name="ManagedMethod"/> this is what the store keys on.</param>
/// <param name="ManagedMethod">The method name without its signature.</param>
/// <param name="FullyQualifiedName">vstest's row-invariant FQN — the store's fallback identity source.</param>
/// <param name="DisplayName">The row's display name; the store upserts rows by it.</param>
/// <param name="Outcome">A vstest <c>TestOutcome</c> name (<c>Passed</c>/<c>Failed</c>/<c>Skipped</c>/…).</param>
/// <param name="Stdout">Captured test output; the store parses this into the row's step trace.</param>
public sealed record TestOutcomeSeedRow(
    string Source,
    string ManagedType,
    string ManagedMethod,
    string FullyQualifiedName,
    string DisplayName,
    string Outcome = "Passed",
    double DurationMs = 1.0,
    string? Stdout = null,
    bool StdoutTruncated = false)
{
    /// <summary>The common case: FQN and display name both derived from the type/method identity.</summary>
    public static TestOutcomeSeedRow For(
        string source, string managedType, string managedMethod,
        string? outcome = null, double durationMs = 1.0, string? stdout = null, bool stdoutTruncated = false)
        => new(source, managedType, managedMethod, $"{managedType}.{managedMethod}", managedMethod,
            outcome ?? "Passed", durationMs, stdout, stdoutTruncated);
}

/// <summary>
/// A client of the server's outcome listener — the benchmark's stand-in for the bundled VSTest
/// logger, so the outcome scenarios can be driven <b>without vstest</b> (issue #714's scenario A
/// note). Writes the same newline-delimited JSON the logger writes (<c>hello</c> / <c>runStart</c> /
/// <c>result</c>… / <c>runComplete</c>) and then closes the connection, which is what makes the
/// listener run its trailing <c>Save</c>.
/// </summary>
/// <remarks>
/// Deliberately hand-rolled rather than referencing <c>Reqnroll.IdeSupport.TestLogger</c>: this is a
/// benchmark-side fixture, and a project reference would tie the benchmark build to a component that
/// ships inside users' test runs.
/// </remarks>
public sealed class TestOutcomeSeedConnection : IDisposable
{
    /// <summary>Mirror of the logger's <c>TestIdentitySeparator</c> (U+001F), which packs <c>runStart.tests</c>.</summary>
    public const char TestIdentitySeparator = (char)0x1f;

    /// <summary>The logger protocol this fixture speaks; the server warns (but still parses) on anything else.</summary>
    public const int ProtocolVersion = 1;

    private readonly TcpClient _client;
    private readonly StreamWriter _writer;

    private TestOutcomeSeedConnection(TcpClient client)
    {
        _client = client;
        _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    /// <summary>Connects to an endpoint as returned by <c>reqnroll/testOutcomes/registerRun</c> (<c>127.0.0.1:port</c>).</summary>
    public static async Task<TestOutcomeSeedConnection> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            throw new ArgumentException($"Endpoint '{endpoint}' is not in 'host:port' form.", nameof(endpoint));

        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint.Substring(0, separator), port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new TestOutcomeSeedConnection(client);
    }

    public Task WriteHelloAsync(string runId, CancellationToken ct = default)
        => WriteLineAsync(new JObject
        {
            ["type"] = "hello",
            ["protocol"] = ProtocolVersion,
            ["runId"] = runId,
            ["runnerPid"] = Environment.ProcessId,
            ["idePid"] = Environment.ProcessId,
            ["connected"] = true,
            ["targetFramework"] = "net10.0",
            ["testRunDirectory"] = string.Empty,
        }, ct);

    /// <summary>
    /// <c>runStart</c>: the identities the IDE shows as "running" while the run is in flight. Each
    /// entry packs <c>source␟managedType␟managedMethod␟fqn␟displayName</c> exactly as the logger's
    /// <c>FormatRunStart</c> does.
    /// </summary>
    public Task WriteRunStartAsync(
        string runId, IReadOnlyList<TestOutcomeSeedRow> tests, int? testCount = null, CancellationToken ct = default)
    {
        var identities = new JArray();
        foreach (var row in tests)
            identities.Add(string.Join(TestIdentitySeparator.ToString(),
                row.Source, row.ManagedType, row.ManagedMethod, row.FullyQualifiedName, row.DisplayName));

        return WriteLineAsync(new JObject
        {
            ["type"] = "runStart",
            ["runId"] = runId,
            ["testCount"] = testCount ?? tests.Count,
            ["sources"] = new JArray(),
            ["tests"] = identities,
        }, ct);
    }

    /// <summary>One <c>result</c> line — the row the IDE's Run lens renders.</summary>
    public Task WriteResultAsync(string runId, TestOutcomeSeedRow row, CancellationToken ct = default)
        => WriteLineAsync(new JObject
        {
            ["type"] = "result",
            ["runId"] = runId,
            ["source"] = row.Source,
            ["managedType"] = row.ManagedType,
            ["managedMethod"] = row.ManagedMethod,
            ["fqn"] = row.FullyQualifiedName,
            ["displayName"] = row.DisplayName,
            ["outcome"] = row.Outcome,
            ["durationMs"] = row.DurationMs,
            ["errorMessage"] = null,
            ["errorStackTrace"] = null,
            ["stdout"] = row.Stdout,
            ["stdoutTruncated"] = row.StdoutTruncated,
        }, ct);

    public Task WriteRunCompleteAsync(string runId, int executed, CancellationToken ct = default)
        => WriteLineAsync(new JObject
        {
            ["type"] = "runComplete",
            ["runId"] = runId,
            ["executed"] = executed,
            ["aborted"] = false,
            ["canceled"] = false,
            ["elapsedMs"] = 0.0,
        }, ct);

    /// <summary>
    /// Flushes and shuts the send side down. The server's read loop then sees EOF, clears the
    /// connection's running marks, and — because results arrived — runs its trailing
    /// <c>TestOutcomePersistence.Save</c>.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        try { _client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
    }

    private async Task WriteLineAsync(JObject message, CancellationToken ct)
    {
        // Formatting.None keeps it one line per message, and JObject escapes the U+001F separators
        // and any control characters in a stdout field (the listener's JObject.Parse unescapes them).
        await _writer.WriteLineAsync(message.ToString(Formatting.None).AsMemory(), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch (Exception) { }
        try { _client.Dispose(); } catch (Exception) { }
    }
}