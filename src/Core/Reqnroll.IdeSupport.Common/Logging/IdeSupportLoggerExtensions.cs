using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>IdeSupportLoggerExtensions</summary>
public static class IdeSupportLoggerExtensions
{
    /// <summary>Determines whether a message at <paramref name="messageLevel"/> would be recorded by this logger.</summary>
    public static bool IsLogging(this IIdeSupportLogger logger, TraceLevel messageLevel)
        => messageLevel <= logger.Level;

    /// <summary>Logs an error-level message.</summary>
    public static void LogError(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Error, message, callerName, callerFilePath);

    /// <summary>Logs a warning-level message.</summary>
    public static void LogWarning(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Warning, message, callerName, callerFilePath);

    /// <summary>Logs an info-level message.</summary>
    public static void LogInfo(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Info, message, callerName, callerFilePath);

    /// <summary>Logs a verbose-level message.</summary>
    public static void LogVerbose(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Verbose, message, callerName, callerFilePath);

    /// <summary>Logs a verbose-level message, evaluating <paramref name="message"/> only if verbose logging is enabled.</summary>
    public static void LogVerbose(this IIdeSupportLogger logger, Func<string> message,
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
    {
        if (!logger.IsLogging(TraceLevel.Verbose)) return;

        Emit(logger, TraceLevel.Verbose, message(), callerName, callerFilePath);
    }

    /// <summary>Reports the exception to telemetry and logs it as an error-level message.</summary>
    public static void LogException(this IIdeSupportLogger logger, IErrorTelemetryService telemetryService, Exception ex,
        string message = "Exception", [CallerMemberName] string callerName = "???",
        [CallerFilePath] string callerFilePath = "?")
    {
        telemetryService.MonitorError(ex);
        LogException(logger, ex, message, callerName, callerFilePath);
    }

    /// <summary>Logs an exception as an error-level message.</summary>
    public static void LogException(this IIdeSupportLogger logger, Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Error, message, callerName, callerFilePath, ex);

    /// <summary>Reports the exception to telemetry (as non-fatal) and logs it as a verbose-level message.</summary>
    public static void LogVerboseException(this IIdeSupportLogger logger, IErrorTelemetryService telemetryService,
        Exception ex, string message = "Exception", [CallerMemberName] string callerName = "???",
        [CallerFilePath] string callerFilePath = "?")
    {
        telemetryService.MonitorError(ex, false);
        Emit(logger, TraceLevel.Verbose, message, callerName, callerFilePath, ex);
    }

    /// <summary>Logs an exception as a verbose-level message, without reporting it to telemetry.</summary>
    public static void LogDebugException(this IIdeSupportLogger logger, Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???", [CallerFilePath] string callerFilePath = "?")
        => Emit(logger, TraceLevel.Verbose, message, callerName, callerFilePath, ex);

    /// <summary>Logs the elapsed time on <paramref name="sw"/> as a verbose trace message, if it exceeds 10ms.</summary>
    public static void Trace(this IIdeSupportLogger logger, Stopwatch sw, string message = "",
        [CallerFilePath] string callerFilePath = "?", [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerName = "???")
    {
        if (sw.ElapsedMilliseconds > 10)
            Trace(logger, $"{sw.Elapsed} {message}", callerFilePath, callerLineNumber, callerName);
    }

    /// <summary>Logs a verbose trace message annotated with the caller's file path and line number.</summary>
    public static void Trace(this IIdeSupportLogger logger, string message = "",
        [CallerFilePath] string callerFilePath = "?", [CallerLineNumber] int callerLineNumber = 0,
        [CallerMemberName] string callerName = "???")
        // Built directly (not via the public LogVerbose(string) overload) so Source is captured
        // from THIS call site's callerFilePath, not IdeSupportLoggerExtensions.cs's own.
        => Emit(logger, TraceLevel.Verbose, $"{message} in {callerFilePath}: line {callerLineNumber}",
            callerName, callerFilePath);

    /// <summary>
    /// Builds and logs a <see cref="LogMessage"/>, deriving <see cref="LogMessage.Source"/> from
    /// the caller's source file name (issue #626) — every public method above is a thin wrapper
    /// so the compiler-supplied <c>[CallerFilePath]</c>/<c>[CallerMemberName]</c> values always
    /// belong to the original call site, not to this file.
    /// </summary>
    private static void Emit(IIdeSupportLogger logger, TraceLevel level, string message, string callerName,
        string callerFilePath, Exception? exception = null)
        => logger.Log(new LogMessage(level, message, callerName, exception, SourceFromFilePath(callerFilePath)));

    private static string? SourceFromFilePath(string callerFilePath)
    {
        if (string.IsNullOrEmpty(callerFilePath) || callerFilePath == "?") return null;
        try
        {
            return Path.GetFileNameWithoutExtension(callerFilePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
