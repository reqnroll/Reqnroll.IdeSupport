using System;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Renders the canonical, shared preamble every <see cref="IIdeSupportLogger"/> sink uses for a
/// <see cref="LogMessage"/> (issue #626) — previously <see cref="SynchronousFileLogger"/> and
/// <see cref="IdeSupportDebugLogger"/> each formatted lines independently and disagreed on shape
/// (timestamp present or not, thread id present or not).
/// </summary>
/// <remarks>
/// The thread id is part of the .NET-specific shape only: it was added to help diagnose a real
/// concurrency bug (issue #554) where telling genuinely concurrent operations apart from ones
/// serialized onto the same thread mattered. The VS Code and Rider ports of this format (which
/// have no comparable multi-threaded-handler concurrency model) render everything but that
/// segment — see <c>lspInspectorLogger.ts</c>'s general-log sink and
/// <c>ReqnrollDebugLogger.kt</c>'s <c>formatTimestamp</c>/<c>formatLine</c>.
/// </remarks>
public static class LogLineFormatter
{
    // Width of the longest TraceLevel name actually ever logged ("Warning"/"Verbose"); Off is a
    // filter sentinel, never a message level, so it's excluded from the width calculation.
    private const int LevelFieldWidth = 7;

    /// <summary>
    /// Formats <paramref name="message"/>'s preamble as
    /// <c>&lt;UTC ISO-8601 timestamp&gt; [&lt;LEVEL&gt;] &lt;origin&gt; (tid=&lt;n&gt;)</c> — callers
    /// append their own <c>": "</c> and message body.
    /// </summary>
    public static string FormatPreamble(LogMessage message) =>
        $"{message.TimeStamp:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{message.Level.ToString().PadRight(LevelFieldWidth)}] " +
        $"{FormatOrigin(message)} (tid={message.ManagedThreadId})";

    /// <summary>
    /// Formats the "where this came from" segment as <c>Source.CallerMethod</c> when both are
    /// known, falling back to whichever one is available, or <c>"?"</c> if neither is.
    /// </summary>
    public static string FormatOrigin(LogMessage message)
    {
        var hasSource = !string.IsNullOrEmpty(message.Source);
        var hasCaller = !string.IsNullOrEmpty(message.CallerMethod);

        if (hasSource && hasCaller) return $"{message.Source}.{message.CallerMethod}";
        if (hasSource) return message.Source!;
        if (hasCaller) return message.CallerMethod;
        return "?";
    }
}
