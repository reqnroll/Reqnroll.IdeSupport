#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.Common;

/// <summary>
/// Helpers for genuinely fire-and-forget work. Discarding a <see cref="Task"/> reference (<c>_
/// = SomeAsyncCall();</c>) is not fire-and-forget: an <c>async</c> method's body runs
/// synchronously on the calling thread up to its first genuine <c>await</c>, so a caller that
/// never observes the returned Task can still be blocked for that entire synchronous prefix, and
/// any exception the task throws goes unobserved. These extensions make both problems explicit:
/// the <see cref="Func{Task}"/> overloads actually defer the work to a thread-pool thread, and
/// every overload logs a fault instead of silently swallowing it.
/// </summary>
public static class FireAndForgetExtensions
{
    /// <summary>
    /// Observes an already-independently-running <paramref name="task"/> (e.g. one started via
    /// <see cref="Task.Run(Func{Task})"/>) without blocking the caller, logging any exception it
    /// throws instead of leaving it unobserved. Does not defer execution by itself — the task
    /// must already be running on its own; use the <see cref="Func{Task}"/> overload when the
    /// work itself still needs to be moved off the calling call stack.
    /// </summary>
    public static void FireAndForget(this Task task, IIdeSupportLogger logger, string context)
    {
        task.ContinueWith(
            t => logger.LogError(
                $"Unhandled exception in fire-and-forget task ({context}): {t.Exception.Flatten().InnerException}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread-pool thread, so its synchronous prefix (up to its
    /// first genuine <c>await</c>) does not run inline on the calling thread — unlike discarding
    /// a <see cref="Task"/> reference, which leaves that prefix on the caller. Any exception
    /// <paramref name="work"/> throws is logged rather than left unobserved.
    /// </summary>
    public static void FireAndForget(this Func<Task> work, IIdeSupportLogger logger, string context) =>
        Task.Run(work).FireAndForget(logger, context);

    /// <summary>Overload of <see cref="FireAndForget(Task, IIdeSupportLogger, string)"/> for call sites that only have a Microsoft.Extensions.Logging <see cref="ILogger"/> available (e.g. VS.Extensibility-hosted services).</summary>
    public static void FireAndForget(this Task task, ILogger logger, string context)
    {
        task.ContinueWith(
            t => logger.LogError(t.Exception.Flatten().InnerException,
                "Unhandled exception in fire-and-forget task ({Context})", context),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Overload of <see cref="FireAndForget(Func{Task}, IIdeSupportLogger, string)"/> for call sites that only have a Microsoft.Extensions.Logging <see cref="ILogger"/> available (e.g. VS.Extensibility-hosted services).</summary>
    public static void FireAndForget(this Func<Task> work, ILogger logger, string context) =>
        Task.Run(work).FireAndForget(logger, context);
}
