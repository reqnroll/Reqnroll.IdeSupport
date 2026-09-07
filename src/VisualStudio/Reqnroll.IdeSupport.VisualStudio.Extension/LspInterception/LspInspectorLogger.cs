using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// An <see cref="ILspMessageInterceptor"/> that writes every LSP message to a shared log
/// file in the format consumed by the
/// <see href="https://microsoft.github.io/language-server-protocol/inspector/">LSP Inspector</see>.
/// Always returns <see cref="LspInterceptorResult.PassThrough"/>.
/// </summary>
internal sealed class LspInspectorLogger : ILspMessageInterceptor, IDisposable
{
    private readonly string _logFilePath;
    private readonly ILogger<LspInspectorLogger> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    // Tracks in-flight request timestamps keyed by JSON-RPC id (as string) so that responses
    // can report round-trip latency via the "latencyMs" envelope field.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingRequests = new();

    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Initialises the logger and creates (or truncates) the log file at
    /// <paramref name="logFilePath"/>.
    /// </summary>
    public LspInspectorLogger(string logFilePath, ILogger<LspInspectorLogger> logger)
    {
        _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            _writer = new StreamWriter(
                new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                Encoding.UTF8,
                bufferSize: 4096,
                leaveOpen: false);
            _writer.AutoFlush = true;
            _logger.LogInformation("LspInspectorLogger: writing LSP Inspector log to {LogFilePath}.", logFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LspInspectorLogger: failed to open log file {LogFilePath}.", logFilePath);
        }
    }

    /// <inheritdoc/>
    public async Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (_writer is null || _disposed)
            return LspInterceptorResult.PassThrough;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(FormatEntry(message)).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LspInspectorLogger: error writing log entry.");
        }
        finally
        {
            _gate.Release();
        }

        return LspInterceptorResult.PassThrough;
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    /// <summary>Internal for testing (issue #628's C#/TypeScript wire-format conformance test).</summary>
    internal string FormatEntry(LspMessage msg)
    {
        // lsp-viewer format: [LSP   - HH:mm:ss] <JSON>\n
        // JSON: {"isLSPMessage":true,"type":"<type>","message":{...},"timestamp":<unix-ms>}
        // Extended fields (ignored by the tool, useful for grep):
        //   "latencyMs" — round-trip ms on response entries
        //   "traceId"   — W3C trace-context trace ID from VS-originated requests

        string type;
        bool isSend = msg.Direction == LspMessageDirection.Send;
        if (msg.IsRequest)
            type = isSend ? "send-request" : "receive-request";
        else if (msg.IsResponse)
            type = isSend ? "send-response" : "receive-response";
        else
            type = isSend ? "send-notification" : "receive-notification";

        long timestampMs = msg.Timestamp.ToUnixTimeMilliseconds();
        string timeLabel = msg.Timestamp.LocalDateTime.ToString("HH:mm:ss");

        var entry = new JObject
        {
            ["isLSPMessage"] = true,
            ["type"]         = type,
            ["message"]      = msg.Body,
            ["timestamp"]    = timestampMs,
        };

        // Track request timestamps for latency computation on matching responses.
        if (msg.IsRequest && msg.Id is not null)
            _pendingRequests[msg.Id.ToString()] = msg.Timestamp;

        // Annotate responses with round-trip latency.
        if (msg.IsResponse && msg.Id is not null &&
            _pendingRequests.TryRemove(msg.Id.ToString(), out var requestTime))
        {
            entry["latencyMs"] = (long)(msg.Timestamp - requestTime).TotalMilliseconds;
        }

        // Extract the W3C trace ID from VS-originated requests so the entry is greppable
        // alongside the server-side debug log (one-way handle: request → server handling).
        var traceparent = msg.Body["traceparent"]?.Value<string>();
        if (traceparent is not null)
        {
            var traceId = ExtractTraceId(traceparent);
            if (traceId is not null)
                entry["traceId"] = traceId;
        }

        // Compact single-line JSON to match the lsp-viewer expectation.
        string json = JsonConvert.SerializeObject(entry, Formatting.None);
        return $"[LSP   - {timeLabel}] {json}\n";
    }

    private static string? ExtractTraceId(string traceparent)
    {
        // W3C traceparent format: "00-<trace-id>-<parent-id>-<flags>"
        var parts = traceparent.Split('-');
        return parts.Length >= 2 ? parts[1] : null;
    }

    // ── IDisposable

    /// <summary>Flushes and closes the log file writer.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var acquired = _gate.Wait(millisecondsTimeout: 500);
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { /* best-effort */ }
        finally
        {
            if (acquired)
                _gate.Release();
        }
        _gate.Dispose();
    }
}
