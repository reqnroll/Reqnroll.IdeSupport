namespace ReqnrollConnector.Logging;

/// <param name="Exception">
/// An optional exception associated with the log entry (issue #628) — kept as a structured field
/// rather than baked into <paramref name="Message"/> via <c>ex.ToString()</c>, so each sink
/// decides for itself how (and whether) to render it, the same separation
/// <c>Reqnroll.IdeSupport.Common.Logging.LogMessage</c> already makes on the .NET host side.
/// </param>
public record Log(LogLevel Level, string Message, Exception? Exception = null);
