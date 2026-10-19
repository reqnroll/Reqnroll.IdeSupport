using System;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Formats elapsed durations for log lines with one consistent unit and precision, so every
/// caller (operation recorders, discovery timing, stopwatch traces) renders the same shape rather
/// than a mix of tenths of a millisecond, truncated whole milliseconds and full
/// <see cref="TimeSpan"/> ticks.
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
