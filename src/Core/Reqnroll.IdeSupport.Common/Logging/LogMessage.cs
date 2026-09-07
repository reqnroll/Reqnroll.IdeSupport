using System;
using System.Diagnostics;
using System.Threading;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>Represents a single log entry captured through <see cref="IIdeSupportLogger"/>.</summary>
/// <param name="Level">The trace/severity level of the log entry.</param>
/// <param name="Message">The log message text.</param>
/// <param name="CallerMethod">The name of the method that produced the log entry.</param>
/// <param name="Exception">An optional exception associated with the log entry.</param>
[DebuggerDisplay("{TimeStamp} {CallerMethod} {Message}")]
public record LogMessage(
    TraceLevel Level,
    string Message,
    string CallerMethod,
    Exception? Exception = default!)
{
    /// <summary>Gets the UTC timestamp when the log entry was created.</summary>
    public DateTimeOffset TimeStamp { get; } = DateTimeOffset.UtcNow;
    /// <summary>Gets the managed thread ID that created the log entry.</summary>
    public int ManagedThreadId { get; } = Thread.CurrentThread.ManagedThreadId;
}