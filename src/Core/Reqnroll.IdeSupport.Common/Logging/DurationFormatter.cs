using System;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Formats elapsed durations for log lines with one consistent unit and precision (issue #627):
/// previously <c>OperationDurationRecorder</c> logged tenths of a millisecond (<c>F1</c>),
/// <c>ConnectorDiscoveryService</c> and <c>IdeSupportTagParser</c> logged whole milliseconds
/// truncated rather than rounded (<c>Stopwatch.ElapsedMilliseconds</c>), and
/// <c>IdeSupportLoggerExtensions.Trace(logger, Stopwatch, ...)</c> logged a full
/// <see cref="TimeSpan"/> (<c>hh:mm:ss.fffffff</c>, tick precision) — four different shapes for
/// the same underlying concept.
/// </summary>
public static class DurationFormatter
{
    /// <summary>Rounds elapsed milliseconds to the nearest whole millisecond (half away from zero) for display.</summary>
    public static long RoundMilliseconds(double elapsedMs) => (long)Math.Round(elapsedMs, MidpointRounding.AwayFromZero);

    /// <summary>Formats elapsed time as <c>"&lt;n&gt;ms"</c>, rounded to the nearest whole millisecond.</summary>
    public static string FormatMilliseconds(double elapsedMs) => $"{RoundMilliseconds(elapsedMs)}ms";

    /// <summary>Formats a <see cref="TimeSpan"/>'s elapsed time as <c>"&lt;n&gt;ms"</c>, rounded to the nearest whole millisecond.</summary>
    public static string FormatMilliseconds(TimeSpan elapsed) => FormatMilliseconds(elapsed.TotalMilliseconds);
}
