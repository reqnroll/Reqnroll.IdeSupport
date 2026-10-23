using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Remembers what one server process was doing, for the <c>Server.Process.ExitedUnexpectedly</c> event if it
/// dies: the requests sent to it that had no response yet, the last request, and its memory use.
/// </summary>
/// <remarks>
/// <para>
/// Registered on both pipelines: a request going to the server (VS's own, or one this extension injects) is
/// recorded, and its response removes it. Responses dropped for an abandoned VS session never reach the
/// interceptors, so their requests stay recorded; the record is therefore bounded and only the most recent
/// methods are reported.
/// </para>
/// <para>
/// Memory is sampled on traffic, at most once per <see cref="SampleInterval"/>, because it can no longer be read
/// once the process has exited. A server that sits idle is not re-sampled, but its memory rarely grows then.
/// </para>
/// <para>
/// Only LSP method names (a fixed vocabulary, checked against <see cref="SafeMethod"/>) and numbers are reported.
/// It never consumes a message.
/// </para>
/// </remarks>
internal sealed class ServerActivityTracker : ILspMessageInterceptor
{
    /// <summary>Property key: requests sent to the server that had no response when it died.</summary>
    public const string InFlightRequestCountKey = "InFlightRequestCount";

    /// <summary>Property key: up to <see cref="MaxReportedMethods"/> distinct methods of those requests, newest first, comma-separated.</summary>
    public const string InFlightMethodsKey = "InFlightMethods";

    /// <summary>Property key: the method of the last request sent to the server.</summary>
    public const string LastRequestMethodKey = "LastRequestMethod";

    /// <summary>Property key: milliseconds from the last request being sent to the exit being reported.</summary>
    public const string LastRequestAgeMsKey = "LastRequestAgeMs";

    /// <summary>Property key: the server's peak working set, in whole megabytes, as of the last sample.</summary>
    public const string PeakWorkingSetMbKey = "PeakWorkingSetMb";

    /// <summary>Property key: the server's private memory, in whole megabytes, as of the last sample.</summary>
    public const string PrivateMemoryMbKey = "PrivateMemoryMb";

    internal const int MaxTracked = 256;
    internal const int MaxReportedMethods = 5;
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    private static readonly Regex SafeMethod = new(@"^[A-Za-z0-9_$/.\-]{1,64}$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Method, long Sequence)> _pending = new();
    private readonly Process? _process;
    private readonly Func<DateTimeOffset> _now;
    private long _sequence;
    private string? _lastMethod;
    private DateTimeOffset _lastRequestAt;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private long? _peakWorkingSetBytes;
    private long? _privateMemoryBytes;

    /// <summary>Creates the tracker for <paramref name="process"/> (<see langword="null"/>: no memory sampling).</summary>
    public ServerActivityTracker(Process? process, Func<DateTimeOffset>? now = null)
    {
        _process = process;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        var now = _now();
        lock (_gate)
        {
            if (message.Direction == LspMessageDirection.Send && message.IsRequest && message.Id is not null)
                RecordRequest(message.Id.ToString(), message.Method!, now);
            else if (message.Direction == LspMessageDirection.Receive && message.IsResponse && message.Id is not null)
                _pending.Remove(message.Id.ToString());
        }

        SampleMemoryIfDue(now);
        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    private void RecordRequest(string id, string method, DateTimeOffset now)
    {
        if (!SafeMethod.IsMatch(method))
            method = "other";

        _pending[id] = (method, ++_sequence);
        _lastMethod = method;
        _lastRequestAt = now;

        if (_pending.Count > MaxTracked)
        {
            var oldest = _pending.OrderBy(p => p.Value.Sequence).First().Key;
            _pending.Remove(oldest);
        }
    }

    private void SampleMemoryIfDue(DateTimeOffset now)
    {
        if (_process is null)
            return;

        lock (_gate)
        {
            if (now - _lastSampleAt < SampleInterval)
                return;
            _lastSampleAt = now;
        }

        try
        {
            _process.Refresh();
            if (_process.HasExited)
                return;
            var peak = _process.PeakWorkingSet64;
            var privateBytes = _process.PrivateMemorySize64;
            lock (_gate)
            {
                _peakWorkingSetBytes = peak;
                _privateMemoryBytes = privateBytes;
            }
        }
        catch (Exception)
        {
            // Exited or inaccessible between the check and the read: keep the previous sample.
        }
    }

    /// <summary>The activity properties as of <paramref name="now"/>; values that are unknown are omitted.</summary>
    public IReadOnlyDictionary<string, object> Describe(DateTimeOffset now)
    {
        var details = new Dictionary<string, object>();
        lock (_gate)
        {
            details[InFlightRequestCountKey] = _pending.Count;
            var methods = _pending.Values
                .OrderByDescending(p => p.Sequence)
                .Select(p => p.Method)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxReportedMethods)
                .ToArray();
            if (methods.Length > 0)
                details[InFlightMethodsKey] = string.Join(",", methods);
            if (_lastMethod is not null)
            {
                details[LastRequestMethodKey] = _lastMethod;
                var age = now - _lastRequestAt;
                if (age >= TimeSpan.Zero)
                    details[LastRequestAgeMsKey] = (long)age.TotalMilliseconds;
            }
            if (_peakWorkingSetBytes is { } peak)
                details[PeakWorkingSetMbKey] = peak / (1024 * 1024);
            if (_privateMemoryBytes is { } privateBytes)
                details[PrivateMemoryMbKey] = privateBytes / (1024 * 1024);
        }
        return details;
    }
}
