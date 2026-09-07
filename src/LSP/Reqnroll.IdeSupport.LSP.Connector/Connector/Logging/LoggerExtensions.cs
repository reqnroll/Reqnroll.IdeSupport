namespace ReqnrollConnector.Logging;

public static class LoggerExtensions
{
    public static void Error(this ILogger logger, string message)
    {
        logger.Log(new Log(LogLevel.Error, message));
    }

    /// <summary>Logs an error with an associated exception (issue #628) — see <see cref="Log.Exception"/>.</summary>
    public static void Error(this ILogger logger, string message, Exception exception)
    {
        logger.Log(new Log(LogLevel.Error, message, exception));
    }

    public static void Info(this ILogger logger, string message)
    {
        logger.Log(new Log(LogLevel.Info, message));
    }
}
