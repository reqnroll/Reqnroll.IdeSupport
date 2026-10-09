using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// A real <see cref="LspInterceptingPipe"/> wired to an in-memory "server" that answers each
/// injected request from a script, so services built over the concrete pipe can be tested
/// end to end (issue #1017). The n-th request gets the n-th scripted reply: a JSON fragment that
/// is spliced into the response (<c>"result":...</c> or <c>"error":...</c>), or <see langword="null"/>
/// for "never answer" (the pipe's owned-request timeout then fires).
/// </summary>
internal sealed class ScriptedServerPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly Pipe _serverToUs = new();
    private readonly Pipe _usToServer = new();
    private readonly ConcurrentQueue<string?> _script = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _requestCount;

    private ScriptedServerPipe(TimeSpan? ownedRequestTimeout)
    {
        Pipe = new LspInterceptingPipe(
            this, Array.Empty<ILspMessageInterceptor>(), Array.Empty<ILspMessageInterceptor>(),
            NullLogger<LspInterceptingPipe>.Instance, ownedRequestTimeout);
        _loop = Task.Run(() => ServeAsync(_stop.Token));
    }

    /// <summary>Creates the pipe and starts its receive/send pumps.</summary>
    public static async Task<ScriptedServerPipe> StartAsync(TimeSpan? ownedRequestTimeout = null)
    {
        var server = new ScriptedServerPipe(ownedRequestTimeout);
        await server.Pipe.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return server;
    }

    public LspInterceptingPipe Pipe { get; }

    /// <summary>Number of requests that reached the server so far.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Every request body (JSON) that reached the server, in order.</summary>
    public ConcurrentQueue<JObject> Requests { get; } = new();

    PipeReader IDuplexPipe.Input => _serverToUs.Reader;
    PipeWriter IDuplexPipe.Output => _usToServer.Writer;

    /// <summary>Queues a successful reply whose <c>result</c> is <paramref name="resultJson"/>.</summary>
    public void ReplyWithResult(string resultJson) => _script.Enqueue("\"result\":" + resultJson);

    /// <summary>Queues a JSON-RPC error reply.</summary>
    public void ReplyWithError(int code, string message) =>
        _script.Enqueue($"\"error\":{{\"code\":{code},\"message\":\"{message}\"}}");

    /// <summary>Queues "no reply at all".</summary>
    public void NeverReply() => _script.Enqueue(null);

    private async Task ServeAsync(CancellationToken ct)
    {
        var reader = _usToServer.Reader;
        var pending = new List<byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(ct).ConfigureAwait(false);
                foreach (var segment in read.Buffer)
                    pending.AddRange(segment.Span.ToArray());
                reader.AdvanceTo(read.Buffer.End);

                while (TryTakeFrame(pending, out var body))
                {
                    var request = JObject.Parse(body);
                    Requests.Enqueue(request);
                    Interlocked.Increment(ref _requestCount);

                    if (!_script.TryDequeue(out var reply) || reply is null)
                        continue;

                    var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{request["id"]!.ToString(Newtonsoft.Json.Formatting.None)},{reply}}}";
                    var bytes = Encoding.UTF8.GetBytes(response);
                    await _serverToUs.Writer.WriteAsync(Encoding.UTF8.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), ct).ConfigureAwait(false);
                    await _serverToUs.Writer.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // test finished
        }
    }

    private static bool TryTakeFrame(List<byte> buffer, out string body)
    {
        body = string.Empty;
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;

        var length = int.Parse(text.Substring(0, headerEnd).Substring("Content-Length:".Length).Trim());
        var headerBytes = Encoding.UTF8.GetByteCount(text.Substring(0, headerEnd + 4));
        if (buffer.Count < headerBytes + length) return false;

        body = Encoding.UTF8.GetString(buffer.GetRange(headerBytes, length).ToArray());
        buffer.RemoveRange(0, headerBytes + length);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Pipe.Dispose();
#pragma warning disable VSTHRD003 // _loop is this class's own background task
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
#pragma warning restore VSTHRD003
        _stop.Dispose();
    }
}
