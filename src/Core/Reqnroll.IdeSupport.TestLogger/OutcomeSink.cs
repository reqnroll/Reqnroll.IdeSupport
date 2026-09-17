using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.TestLogger;

/// <summary>
/// Where the logger's NDJSON lines go. Two sinks, both optional and both fire-and-forget: a TCP
/// loopback connection to the IDE that registered the logger (the real channel), and an append-only
/// file (troubleshooting mirror, also what the spike used). Every failure mode ends in "stop sending",
/// never in an exception reaching vstest — a logger must not slow down or fail a test run.
/// </summary>
internal sealed class OutcomeSink : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private TcpClient? _client;
    private Stream? _stream;
    private string? _filePath;
    private bool _tcpFaulted;

    /// <summary>True once a TCP connection is open (or the file sink is set); false means every write is dropped.</summary>
    public bool IsActive
    {
        get { lock (_gate) return (_stream is not null && !_tcpFaulted) || _filePath is not null; }
    }

    public string? FilePath => _filePath;

    /// <summary>
    /// Connects to <paramref name="endpoint"/> (<c>host:port</c>, loopback expected). Returns false and
    /// stays inert on any failure — bad format, refused, timeout.
    /// </summary>
    public bool TryConnect(string endpoint)
    {
        if (!TryParseEndpoint(endpoint, out var address, out var port))
            return false;

        TcpClient? client = null;
        try
        {
            client = new TcpClient(address.AddressFamily) { NoDelay = true, SendTimeout = (int)SendTimeout.TotalMilliseconds };
            var connect = client.ConnectAsync(address, port);

            // Task.Wait rethrows (as AggregateException) when the task is already faulted by the time
            // the timeout elapses — a fast connection-refused finishes well inside ConnectTimeout, so
            // that path is a genuine "didn't connect" outcome here, not an error to propagate.
            bool connected;
            try
            {
                connected = connect.Wait(ConnectTimeout) && client.Connected;
            }
            catch (AggregateException)
            {
                connected = false;
            }

            if (!connected)
            {
                // Observe the eventual fault (of a still-pending timed-out connect) so it never
                // surfaces as an UnobservedTaskException inside the runner.
                _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                client.Dispose();
                return false;
            }

            lock (_gate)
            {
                _client = client;
                _stream = client.GetStream();
                _tcpFaulted = false;
            }
            return true;
        }
        catch (Exception)
        {
            // Any other failure (constructing the client, GetStream, ...): the client is either not
            // yet assigned to _client or never will be, so dispose it here rather than leaking it.
            client?.Dispose();
            return false;
        }
    }

    public void SetFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_gate) _filePath = path;
        }
        catch (Exception)
        {
            // Unwritable path: silently no file sink.
        }
    }

    /// <summary>Writes one NDJSON line to every active sink. Never throws.</summary>
    public void Write(string line)
    {
        lock (_gate)
        {
            if (_stream is not null && !_tcpFaulted)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                catch (Exception)
                {
                    // IDE went away mid-run (or the pipe is full past the send timeout): stop trying
                    // for the rest of this run rather than paying a timeout per result.
                    _tcpFaulted = true;
                }
            }

            if (_filePath is not null)
            {
                try { File.AppendAllText(_filePath, line); }
                catch (Exception) { /* best-effort mirror */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch (Exception) { }
            try { _client?.Dispose(); } catch (Exception) { }
            _stream = null;
            _client = null;
        }
    }

    internal static bool TryParseEndpoint(string? endpoint, out IPAddress address, out int port)
    {
        address = IPAddress.Loopback;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        var separator = endpoint!.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1) return false;

        var host = endpoint.Substring(0, separator).Trim('[', ']');
        if (!IPAddress.TryParse(host, out address)) return false;
        return int.TryParse(endpoint.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
               && port is > 0 and <= ushort.MaxValue;
    }
}
