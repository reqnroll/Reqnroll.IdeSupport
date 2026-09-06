using System.Text;

namespace ReqnrollConnector.Logging;

/// <summary>
/// Appends log messages to a per-process file (issue #628) — previously the Connector had no
/// persistent log at all: <see cref="ConsoleLogger"/> only ever wrote to stdout/stderr, which the
/// LSP server discards beyond the one summary line it logs itself
/// (<c>ConnectorDiscoveryService.RunDiscoveryIfNeededAsync</c>), so a Connector crash mid-discovery
/// left no artifact of its own to diagnose from.
/// </summary>
/// <remarks>
/// Opens, appends, and closes the file on every call — the same pattern
/// <c>Reqnroll.IdeSupport.Common.Logging.SynchronousFileLogger</c> uses on the .NET host side —
/// rather than holding one writer open for the process's lifetime: the Connector logs at nowhere
/// near the volume the LSP server does (one discovery run), so the per-call open/close cost is
/// negligible, and it means nothing needs disposing and the file is always immediately readable
/// by another process (a support engineer tailing it, or a test) rather than locked for however
/// long this process happens to keep running.
/// <para>
/// Deliberately does not use <see cref="Logger{T}"/> — that base class's per-level
/// <c>GetTextWriter</c> split (stdout for <see cref="LogLevel.Info"/>, stderr for
/// <see cref="LogLevel.Error"/>) exists for <see cref="ConsoleLogger"/> alone: <c>Info</c> also
/// carries the discovery result JSON that the LSP server parses off stdout
/// (<c>Runner.PrintResult</c>), so that channel must stay byte-for-byte what it always was. A file
/// has no such constraint — every level goes to the same file here, with its own timestamp/level
/// preamble, and open/write failures are swallowed so file logging can never break discovery.
/// </para>
/// </remarks>
public sealed class FileLogger : ILogger
{
    private readonly object _writeLock = new();

    /// <summary>Gets the log file path, or <c>null</c> if it could not be resolved.</summary>
    public string? LogFilePath { get; }

    /// <summary>Resolves the log file path, swallowing any failure so construction never throws.</summary>
    public FileLogger(string ide = "lsp", string role = "connector")
    {
        try
        {
            var dir = ConnectorLogPaths.ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            // Process.GetCurrentProcess().Id rather than Environment.ProcessId (.NET 5+ only) -
            // this project multi-targets down to net462.
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            LogFilePath = Path.Combine(dir, $"reqnroll-{ide}-{role}-{DateTime.UtcNow:yyyyMMdd}-{pid}.log");
        }
        catch
        {
            // Best-effort — file logging must never break discovery.
            LogFilePath = null;
        }
    }

    /// <summary>Formats and appends the log message to the file, swallowing any write errors.</summary>
    public void Log(Log log)
    {
        if (LogFilePath is null) return;

        try
        {
            var preamble = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{log.Level.ToString().PadRight(5)}]";
            var line = LogFormatting.AppendExceptionDetail($"{preamble} {log.Message}", log.Exception) + Environment.NewLine;

            lock (_writeLock)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort — file logging must never break discovery.
        }
    }
}
