using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>The closed set of <c>CrashKind</c> values: which report the .NET runtime printed to stderr as the process died.</summary>
public static class CrashKind
{
    /// <summary>An exception no code caught (<c>Unhandled exception.</c>).</summary>
    public const string UnhandledException = "UnhandledException";

    /// <summary>The stack overflowed (<c>Stack overflow.</c>); the runtime prints frames but no exception.</summary>
    public const string StackOverflow = "StackOverflow";

    /// <summary>A runtime-fatal exception such as an access violation (<c>Fatal error.</c>).</summary>
    public const string FatalError = "FatalError";

    /// <summary><c>Environment.FailFast</c> or a runtime abort (<c>Process terminated.</c>).</summary>
    public const string FailFast = "FailFast";
}

/// <summary>
/// What the .NET runtime printed to stderr as a server process died, reduced to telemetry-safe values: the kind
/// of report, and the outermost and innermost exception (type, scrubbed message, product-only frames). Built by
/// <see cref="CrashReportParser"/>; added to <see cref="TelemetryEvents.ServerProcessExitedUnexpectedly"/> by
/// the client that read the stderr.
/// </summary>
public sealed class CrashReport
{
    /// <summary>Property key: the <see cref="CrashKind"/> value.</summary>
    public const string CrashKindKey = "CrashKind";

    /// <summary>Property key: full type name of the outermost exception.</summary>
    public const string ExceptionTypeKey = "ExceptionType";

    /// <summary>Property key: the outermost exception's message, scrubbed by <see cref="TelemetryScrubber.ScrubMessage"/>.</summary>
    public const string MessageKey = "Message";

    /// <summary>Property key: the outermost exception's product frames (the <c>StackFrames</c> contract of <c>Error.UnhandledException</c>).</summary>
    public const string StackFramesKey = "StackFrames";

    /// <summary>Property key: full type name of the innermost exception, when there is an inner exception.</summary>
    public const string InnerExceptionTypeKey = "InnerExceptionType";

    /// <summary>Property key: the innermost exception's scrubbed message.</summary>
    public const string InnerMessageKey = "InnerMessage";

    /// <summary>Property key: the innermost exception's product frames.</summary>
    public const string InnerStackFramesKey = "InnerStackFrames";

    /// <summary>Property key: how many exceptions are nested inside the outermost one (1 = one inner exception).</summary>
    public const string InnerExceptionCountKey = "InnerExceptionCount";

    internal CrashReport(string kind, CrashedException outer, CrashedException inner, int innerCount)
    {
        Kind = kind;
        Outer = outer;
        Inner = inner;
        InnerExceptionCount = innerCount;
    }

    /// <summary>One of the <see cref="CrashKind"/> values.</summary>
    public string Kind { get; }

    /// <summary>The outermost exception (for a stack overflow: no type or message, only frames).</summary>
    public CrashedException Outer { get; }

    /// <summary>The innermost exception, or <see langword="null"/> when there was none.</summary>
    public CrashedException? Inner { get; }

    /// <summary>The number of exceptions nested inside <see cref="Outer"/>.</summary>
    public int InnerExceptionCount { get; }

    /// <summary>The report as event properties; values that are unknown are omitted.</summary>
    public IReadOnlyDictionary<string, object> ToProperties()
    {
        var properties = new Dictionary<string, object> { [CrashKindKey] = Kind };
        Add(properties, ExceptionTypeKey, Outer.Type);
        Add(properties, MessageKey, Outer.Message);
        Add(properties, StackFramesKey, Outer.StackFrames);
        if (Inner != null)
        {
            properties[InnerExceptionCountKey] = InnerExceptionCount;
            Add(properties, InnerExceptionTypeKey, Inner.Type);
            Add(properties, InnerMessageKey, Inner.Message);
            Add(properties, InnerStackFramesKey, Inner.StackFrames);
        }
        return properties;

        static void Add(Dictionary<string, object> target, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
                target[key] = value;
        }
    }
}

/// <summary>One exception of a <see cref="CrashReport"/>; every member may be <see langword="null"/>.</summary>
public sealed class CrashedException
{
    internal CrashedException(string type, string message, string stackFrames)
    {
        Type = type;
        Message = message;
        StackFrames = stackFrames;
    }

    /// <summary>Full type name, as the runtime printed it.</summary>
    public string? Type { get; }

    /// <summary>The message after <see cref="TelemetryScrubber.ScrubMessage"/>.</summary>
    public string? Message { get; }

    /// <summary>Product-only frames; see <see cref="ExceptionStackSanitizer"/>.</summary>
    public string? StackFrames { get; }
}

/// <summary>
/// Parses the crash report the .NET runtime writes to stderr when a process dies (an unhandled exception, a
/// stack overflow, a fatal runtime error or <c>Environment.FailFast</c>) into a <see cref="CrashReport"/>.
/// Nothing from the raw text is kept except full type names, messages passed through
/// <see cref="TelemetryScrubber.ScrubMessage"/>, and frames reduced like <see cref="ExceptionStackSanitizer"/>
/// (product namespaces only, no paths, parameters or columns).
/// </summary>
/// <remarks>
/// The unhandled-exception text is <c>Exception.ToString()</c>: the outer header, then one <c> ---&gt; </c> header
/// per nested exception, then the innermost exception's frames, an <c>--- End of inner exception stack trace ---</c>
/// line, the next one out's frames, and so on, ending with the outermost exception's frames.
/// </remarks>
public static class CrashReportParser
{
    private const string UnhandledPrefix = "Unhandled exception.";
    private const string FatalPrefix = "Fatal error.";
    private const string StackOverflowPrefix = "Stack overflow.";
    private const string FailFastPrefix = "Process terminated.";
    private const string InnerPrefix = "--->";
    private const string EndOfInnerStack = "--- End of inner exception stack trace ---";

    private static readonly Regex FramePattern = new(
        // Generic arguments print in brackets after the method name, as Method[T] or, in a stack-overflow or
        // FailFast report, Method[[System.__Canon, System.Private.CoreLib, ...]], which contains spaces.
        @"^\s*at (?<name>[^\s(\[]+)(?:\[[^(]*\])?\([^)]*\)(?: in .*:line (?<line>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(200));

    private static readonly Regex InnerOrdinalPattern = new(
        @"^\(Inner Exception #\d+\)\s*",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex TypeNamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.`+]*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether <paramref name="line"/> starts one of the runtime's crash reports.</summary>
    public static bool IsCrashHeader(string? line) =>
        line != null && KindOf(line) != null;

    /// <summary>
    /// Parses the lines of the last crash report in <paramref name="lines"/>, or returns <see langword="null"/> when
    /// they hold none. Never throws.
    /// </summary>
    public static CrashReport? Parse(IReadOnlyList<string>? lines)
    {
        try
        {
            return ParseCore(lines);
        }
        catch (Exception)
        {
            // Includes RegexMatchTimeoutException: a report must never break the exit path that reads it.
            return null;
        }
    }

    private static CrashReport ParseCore(IReadOnlyList<string> lines)
    {
        if (lines == null)
            return null;

        var start = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (IsCrashHeader(lines[i]))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return null;

        var headerLine = lines[start].TrimStart();
        var kind = KindOf(headerLine);
        var prefix = kind switch
        {
            CrashKind.UnhandledException => UnhandledPrefix,
            CrashKind.FatalError => FatalPrefix,
            CrashKind.FailFast => FailFastPrefix,
            _ => StackOverflowPrefix,
        };

        // Header text per exception, outermost first. A stack overflow has no exception header.
        var headers = new List<string>();
        var rest = headerLine.Substring(prefix.Length).Trim();
        if (kind != CrashKind.StackOverflow)
            headers.Add(rest);

        // Frame sections, innermost first, split at each end-of-inner-stack line.
        var sections = new List<List<string>> { new() };
        var inFrames = false;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i] ?? string.Empty;
            var trimmed = line.Trim();
            var frame = FramePattern.Match(line);
            if (frame.Success)
            {
                inFrames = true;
                var number = frame.Groups["line"].Success
                    ? int.Parse(frame.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
                    : 0;
                var formatted = ExceptionStackSanitizer.FormatProductFrameText(frame.Groups["name"].Value, number);
                var section = sections[sections.Count - 1];
                // A stack overflow prints its repeating block once, but consecutive repeats of one frame add nothing.
                if (section.Count == 0 || section[section.Count - 1] != formatted)
                    section.Add(formatted);
            }
            else if (trimmed == EndOfInnerStack)
            {
                sections.Add(new List<string>());
            }
            else if (!inFrames && kind != CrashKind.StackOverflow && trimmed.StartsWith(InnerPrefix, StringComparison.Ordinal))
            {
                headers.Add(InnerOrdinalPattern.Replace(trimmed.Substring(InnerPrefix.Length).Trim(), string.Empty));
            }
            else if (!inFrames && headers.Count > 0 && trimmed.Length > 0)
            {
                // A message spanning several lines continues the latest header.
                headers[headers.Count - 1] += " " + trimmed;
            }
        }

        var outerFrames = sections[sections.Count - 1];
        if (kind == CrashKind.StackOverflow)
            return new CrashReport(kind, new CrashedException(null, null, Frames(outerFrames)), null, 0);

        var outer = ToException(kind == CrashKind.FailFast ? null : headers[0], Frames(outerFrames), failFastMessage: kind == CrashKind.FailFast ? headers[0] : null);
        if (headers.Count < 2)
            return new CrashReport(kind, outer, null, 0);

        var innerFrames = sections.Count > 1 ? Frames(sections[0]) : null;
        var inner = ToException(headers[headers.Count - 1], innerFrames, failFastMessage: null);
        return new CrashReport(kind, outer, inner, headers.Count - 1);
    }

    private static string KindOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(UnhandledPrefix, StringComparison.Ordinal))
            return CrashKind.UnhandledException;
        if (trimmed.StartsWith(StackOverflowPrefix, StringComparison.Ordinal))
            return CrashKind.StackOverflow;
        if (trimmed.StartsWith(FatalPrefix, StringComparison.Ordinal))
            return CrashKind.FatalError;
        if (trimmed.StartsWith(FailFastPrefix, StringComparison.Ordinal))
            return CrashKind.FailFast;
        return null;
    }

    private static string Frames(List<string> section) =>
        section.Count == 0 ? null : ExceptionStackSanitizer.JoinFrames(section, ExceptionStackSanitizer.DefaultMaxFrames);

    // "Namespace.TypeName: message" -> (type, scrubbed message). A header that does not start with a type name
    // keeps no type, and its text is treated as the message.
    private static CrashedException ToException(string header, string frames, string failFastMessage)
    {
        if (header == null)
            return new CrashedException(null, TelemetryScrubber.ScrubMessage(NullIfEmpty(failFastMessage)), frames);

        var separator = header.IndexOf(": ", StringComparison.Ordinal);
        var candidate = separator > 0 ? header.Substring(0, separator) : header;
        if (!TypeNamePattern.IsMatch(candidate))
            return new CrashedException(null, TelemetryScrubber.ScrubMessage(NullIfEmpty(header)), frames);

        var message = separator > 0 ? header.Substring(separator + 2) : null;
        return new CrashedException(candidate, TelemetryScrubber.ScrubMessage(NullIfEmpty(message)), frames);
    }

    private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Collects a process's stderr lines from the last crash-report header onwards (bounded), so a client can parse
/// the report once the process has died. Lines before any header are not kept, so a long-running process costs
/// nothing. Thread-safe: <see cref="OnLine"/> is called from the stderr reader, the rest from the exit handler.
/// </summary>
public sealed class CrashReportCollector
{
    /// <summary>The most lines kept after a header; enough for any product stack, bounded for deep recursion.</summary>
    public const int MaxLines = 400;

    /// <summary>Lines longer than this are cut, so one huge message cannot hold much memory.</summary>
    public const int MaxLineLength = 2048;

    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _capturing;

    /// <summary>
    /// Records one stderr line; <see langword="null"/> marks the end of the stream (as
    /// <c>DataReceivedEventArgs.Data</c> does).
    /// </summary>
    public void OnLine(string? line)
    {
        if (line == null)
        {
            _ended.TrySetResult(true);
            return;
        }

        lock (_gate)
        {
            if (CrashReportParser.IsCrashHeader(line))
            {
                _lines.Clear();
                _capturing = true;
            }
            if (_capturing && _lines.Count < MaxLines)
                _lines.Add(line.Length > MaxLineLength ? line.Substring(0, MaxLineLength) : line);
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the end of the stream; <see langword="true"/> when it was reached.</summary>
    public bool WaitForEnd(TimeSpan timeout)
    {
        try
        {
            return _ended.Task.Wait(timeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The crash report collected so far, or <see langword="null"/> when no header was seen.</summary>
    public CrashReport? Parse()
    {
        string[] lines;
        lock (_gate)
        {
            if (!_capturing)
                return null;
            lines = _lines.ToArray();
        }
        return CrashReportParser.Parse(lines);
    }
}
