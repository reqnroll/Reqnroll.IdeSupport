namespace ReqnrollConnector.Logging;

/// <summary>Fans a log entry out to every composed <see cref="ILogger"/> sink (issue #628).</summary>
public sealed class CompositeLogger : ILogger
{
    private readonly ILogger[] _loggers;

    public CompositeLogger(params ILogger[] loggers) => _loggers = loggers;

    public void Log(Log log)
    {
        foreach (var logger in _loggers)
            logger.Log(log);
    }
}
