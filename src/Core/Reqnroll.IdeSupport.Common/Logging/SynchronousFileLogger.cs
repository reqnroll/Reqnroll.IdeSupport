using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Reqnroll.IdeSupport.Common.Logging;

/// <summary>
/// Appends log messages to a per-process file, synchronously on the calling thread. Historically
/// this was one of two logger implementations (the other, <c>AsynchronousFileLogger</c>, queued
/// messages through a <c>Channel</c> drained by a background worker) ported over from the legacy
/// Reqnroll.VisualStudio extension. In practice every real call site — the VS extension, the LSP
/// server, and the protocol logger — always used this synchronous one; the async variant was never
/// actually instantiated outside its own tests. Rather than keep an unused second implementation
/// "just in case," it's been folded away: async logging risks losing exactly the last messages
/// before an unclean shutdown (the process getting killed rather than cleanly disposed never drains
/// whatever's still queued), which matters more for a debug/crash log than the throughput this class
/// gives up by writing on the calling thread. <see cref="WriteLogMessage"/>'s file append is guarded
/// by a lock — with many LSP handlers logging concurrently from the thread-pool onto one shared
/// instance, unsynchronized <c>File.AppendAllText</c> calls could otherwise interleave/tear each
/// other's writes.
/// </summary>
public class SynchronousFileLogger : IIdeSupportLogger
{
    private readonly IFileSystemForIDE _fileSystem;
    private readonly object _writeLock = new();

    /// <summary>Initializes a new instance of the <see cref="SynchronousFileLogger"/> class.</summary>
    public SynchronousFileLogger(string ide = "vs", string role = "ext", TraceLevel level = TraceLevel.Warning)
    {
        _fileSystem = new FileSystemForIDE();
        Level = ApplyDebugEnvironmentOverride(level);
        LogFilePath = GetLogFile(ide, role);
        EnsureLogFolder();
    }

    /// <summary>Gets the log file path.</summary>
    public string LogFilePath { get; private set; }
    /// <summary>Gets the minimum trace level that will be written to the log.</summary>
    public TraceLevel Level { get; }

    // Matches the legacy Reqnroll.VisualStudio DeveroomDebugLogger behavior: REQNROLLVS_DEBUG=1/true
    // forces Verbose, any other value that parses as a TraceLevel name overrides the configured level.
    private static TraceLevel ApplyDebugEnvironmentOverride(TraceLevel level)
    {
        var env = Environment.GetEnvironmentVariable("REQNROLLVS_DEBUG");
        if (env == null) return level;

        if (env.Equals("1") || env.Equals("true", StringComparison.InvariantCultureIgnoreCase))
            return TraceLevel.Verbose;

        return Enum.TryParse<TraceLevel>(env, true, out var envLevel) ? envLevel : level;
    }

    /// <summary>Writes the log message to the file synchronously, swallowing any write errors.</summary>
    public void Log(LogMessage message)
    {
        if (message.Level > Level) return;

        try
        {
            WriteLogMessage(message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"Error writing to the {LogFilePath}");
        }
    }

    internal static string GetLogFile(string ide, string role)
    {
        // PID is included so that concurrent instances (e.g. two VS windows, each with its own
        // extension host and LSP server process) never append to the same log file. The date is
        // always the UTC date (previously DEBUG builds used UtcNow but RELEASE builds used the
        // local date, so the daily rollover boundary silently differed between configurations).
        var pid = Process.GetCurrentProcess().Id;
        return Path.Combine(ReqnrollLogPaths.ResolveLogDirectory(),
#if DEBUG
            $"reqnroll-{ide}-{role}-debug-{DateTime.UtcNow:yyyyMMdd}-{pid}.log");
#else
            $"reqnroll-{ide}-{role}-{DateTime.UtcNow:yyyyMMdd}-{pid}.log");
#endif
    }

    /// <summary>Formats a log message and appends it to the log file.</summary>
    private void WriteLogMessage(LogMessage message)
    {
        // Indent continuation lines so multi-line messages (e.g. connector JSON, stack traces)
        // remain visually grouped without losing the structured prefix on the first line.
        var body = message.Message.Replace("\r\n", "\n").Replace("\n", "\n    ");
        var content = $"{LogLineFormatter.FormatPreamble(message)}: {body}";
        if (message.Exception != null) content += $"\n    : {message.Exception}".Replace("\n", "\n    ");
        content += Environment.NewLine;

        lock (_writeLock)
        {
            _fileSystem.File.AppendAllText(LogFilePath, content, Encoding.UTF8);
        }
    }

    /// <summary>Resolves <see cref="LogFilePath"/> to a full path and creates its containing folder if missing.</summary>
    private void EnsureLogFolder()
    {
        LogFilePath = Path.GetFullPath(LogFilePath);
        var logFolder = Path.GetDirectoryName(LogFilePath);
        if (!_fileSystem.Directory.Exists(logFolder))
            _fileSystem.Directory.CreateDirectory(logFolder);
    }
}
