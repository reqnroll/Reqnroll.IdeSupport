using System;
using System.Diagnostics;
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
        [CallerMemberName] string callerName = "???")
    {
        var msg = new LogMessage(TraceLevel.Error, message, callerName);
        logger.Log(msg);
    }

    /// <summary>Logs a warning-level message.</summary>
    public static void LogWarning(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???")
    {
        var msg = new LogMessage(TraceLevel.Warning, message, callerName);
        logger.Log(msg);
    }

    /// <summary>Logs an info-level message.</summary>
    public static void LogInfo(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???")
    {
        var msg = new LogMessage(TraceLevel.Info, message, callerName);
        logger.Log(msg);
    }

    /// <summary>Logs a verbose-level message.</summary>
    public static void LogVerbose(this IIdeSupportLogger logger, string message,
        [CallerMemberName] string callerName = "???")
    {
        var msg = new LogMessage(TraceLevel.Verbose, message, callerName);
        logger.Log(msg);
    }

    /// <summary>Logs a verbose-level message, evaluating <paramref name="message"/> only if verbose logging is enabled.</summary>
    public static void LogVerbose(this IIdeSupportLogger logger, Func<string> message,
        [CallerMemberName] string callerName = "???")
    {
        if (!logger.IsLogging(TraceLevel.Verbose)) return;

        var msg = new LogMessage(TraceLevel.Verbose, message(), callerName);
        logger.Log(msg);
    }

    /// <summary>Reports the exception to telemetry and logs it as an error-level message.</summary>
    public static void LogException(this IIdeSupportLogger logger, IErrorTelemetryService telemetryService, Exception ex,
        string message = "Exception", [CallerMemberName] string callerName = "???")
    {
        telemetryService.MonitorError(ex);
        LogException(logger, ex, message, callerName);
    }

    /// <summary>Logs an exception as an error-level message.</summary>
    public static void LogException(this IIdeSupportLogger logger, Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???")
    {
        //Debug.Fail(ex.ToString());
        var msg = new LogMessage(TraceLevel.Error, message, callerName, ex);
        logger.Log(msg);
    }

    /// <summary>Reports the exception to telemetry (as non-fatal) and logs it as a verbose-level message.</summary>
    public static void LogVerboseException(this IIdeSupportLogger logger, IErrorTelemetryService telemetryService,
        Exception ex, string message = "Exception", [CallerMemberName] string callerName = "???")
    {
        telemetryService.MonitorError(ex, false);
        var msg = new LogMessage(TraceLevel.Verbose, message, callerName, ex);
        logger.Log(msg);
    }

    /// <summary>Logs an exception as a verbose-level message, without reporting it to telemetry.</summary>
    public static void LogDebugException(this IIdeSupportLogger logger, Exception ex, string message = "Exception",
        [CallerMemberName] string callerName = "???")
    {
        var msg = new LogMessage(TraceLevel.Verbose, message, callerName, ex);
        logger.Log(msg);
    }


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
    {
        logger.LogVerbose($"{message} in {callerFilePath}: line {callerLineNumber}", callerName);
    }
}
