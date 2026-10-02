using System;
using System.Diagnostics;
using System.Threading;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>Represents a single log entry captured through <see cref="IIdeSupportLogger"/>.</summary>
/// <param name="Level">The trace/severity level of the log entry.</param>
/// <param name="Message">The log message text.</param>
/// <param name="CallerMethod">The name of the method that produced the log entry.</param>
/// <param name="Exception">An optional exception associated with the log entry.</param>
/// <param name="Source">
/// The originating type/file name, when known — the source file's name (without extension) for
/// call sites reached through <see cref="IdeSupportLoggerExtensions"/>, or the logging category
/// for call sites reached through the <c>Microsoft.Extensions.Logging</c> bridge
/// (<see cref="IdeSupportLoggerAdapter"/>). Rendered by <see cref="LogLineFormatter"/> alongside
/// <see cref="CallerMethod"/> so a log line identifies where it came from, not just which method.
/// </param>
[DebuggerDisplay("{TimeStamp} {Source} {CallerMethod} {Message}")]
public record LogMessage(
    TraceLevel Level,
    string Message,
    string CallerMethod,
    Exception? Exception = default!,
    string? Source = default!)
{
    /// <summary>Gets the UTC timestamp when the log entry was created.</summary>
    public DateTimeOffset TimeStamp { get; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the managed thread ID that created the log entry.</summary>
    public int ManagedThreadId { get; } = Thread.CurrentThread.ManagedThreadId;
}