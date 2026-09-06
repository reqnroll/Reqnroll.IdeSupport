namespace ReqnrollConnector.Logging;

/// <summary>
/// Shared body formatting for every <see cref="ILogger"/> sink (issue #628) — a static helper
/// rather than a shared base class, since <see cref="ConsoleLogger"/> (via <see cref="Logger{T}"/>)
/// and <see cref="FileLogger"/> have different enough writer/lifetime needs (split by level vs. one
/// shared writer regardless of level; never-throw vs. propagate) that forcing them under one base
/// class would cost more than it'd share.
/// </summary>
internal static class LogFormatting
{
    /// <summary>
    /// Appends an indented exception block to <paramref name="message"/> when present, matching
    /// <c>Reqnroll.IdeSupport.Common.Logging.SynchronousFileLogger</c>'s convention on the .NET
    /// host side (kept in sync by hand — this project can't reference that assembly, see
    /// <see cref="ConnectorLogPaths"/>'s remarks for why).
    /// </summary>
    public static string AppendExceptionDetail(string message, Exception? exception)
    {
        if (exception is null) return message;
        return message + $"\n    : {exception}".Replace("\n", "\n    ");
    }
}
