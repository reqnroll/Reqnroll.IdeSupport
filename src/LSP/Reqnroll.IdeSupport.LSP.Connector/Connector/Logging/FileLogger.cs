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
/// <para>
/// <b>Buffered until something's worth persisting (issue #637):</b> the original version of this
/// class wrote every line immediately, unconditionally — every routine, uneventful discovery run
/// left its own file behind regardless of the ambient log level, unlike every other logger in this
/// family (all gated by verbosity, defaulting to quiet in Release). In practice this meant a single
/// IDE session touching a handful of projects could leave a dozen-plus tiny files behind, none of
/// which anyone would ever need to open. Legacy Reqnroll.VisualStudio never had a Connector-side log
/// at all and relied solely on the extension's own log, which is fine for the common case — the
/// value here is specifically in the rare case something actually goes wrong.
/// </para>
/// <para>
/// So: every line is always buffered in memory (cheap — a discovery run's own trace is at most a
/// few hundred short lines), but nothing touches disk until either <paramref name="alwaysWrite"/>
/// was requested at construction (<c>--file-log</c>, which <c>OutProcReqnrollConnector</c> adds
/// whenever the server's own <c>--log-level</c> is Info or more verbose — see <c>Program.cs</c> for
/// why that's a separate flag from the pre-existing <c>--debug</c>, not the same one), or a
/// <see cref="LogLevel.Error"/> entry is logged, at which point the whole buffer (everything logged
/// so far, giving the context leading up to the error, not just the error line itself) is flushed
/// and every subsequent line is written live. A run that never logs an error and wasn't asked to
/// write leaves no file at all — no directory is even created.
/// </para>
/// <para>
/// The trade-off: a crash severe enough to bypass <c>Runner</c>'s own top-level catch (which is
/// where <see cref="LogLevel.Error"/> gets logged for every path that would otherwise exit
/// non-zero) loses whatever was only buffered and never flushed. Accepted deliberately — that kind
/// of catastrophic failure (a stack overflow, a native crash) wouldn't reliably flush a live-written
/// file either in many cases, and is rare enough that trading it off against file clutter on every
/// normal run is the right call for the common case.
/// </para>
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
    private readonly List<string> _buffer = new();
    private readonly bool _alwaysWrite;
    private readonly string _ide;
    private readonly string _role;
    private bool _flushed;

    /// <summary>
    /// Gets the log file path once resolved, or <c>null</c> before anything has been written to it
    /// (either because nothing has happened yet, or because the run never logged an error and
    /// <paramref name="alwaysWrite"/> was never requested — see the class remarks).
    /// </summary>
    public string? LogFilePath { get; private set; }

    /// <param name="alwaysWrite">
    /// When <see langword="true"/> (<c>--file-log</c>, or <c>--debug</c> — see <c>Program.cs</c>),
    /// every line is written to the file immediately as it's logged, matching this class's original
    /// always-on behavior. When <see langword="false"/> (the default), lines are buffered in memory
    /// and the file is only created — and the buffer flushed to it — once a
    /// <see cref="LogLevel.Error"/> entry occurs.
    /// </param>
    public FileLogger(bool alwaysWrite, string ide = "lsp", string role = "connector")
    {
        _alwaysWrite = alwaysWrite;
        _ide = ide;
        _role = role;
    }

    /// <summary>Buffers (or writes, once active) the log message, swallowing any write errors.</summary>
    public void Log(Log log)
    {
        var preamble = $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{log.Level.ToString().PadRight(5)}]";
        var line = LogFormatting.AppendExceptionDetail($"{preamble} {log.Message}", log.Exception) + Environment.NewLine;

        lock (_writeLock)
        {
            if (_flushed)
            {
                WriteLine(line);
                return;
            }

            _buffer.Add(line);

            // First activation, either because file logging was requested outright, or because
            // this is the error that makes the buffered context worth keeping.
            if (_alwaysWrite || log.Level == LogLevel.Error)
                Activate();
        }
    }

    // Caller must hold _writeLock. Resolves the file path (first activation only) and writes out
    // everything buffered so far, including whichever line triggered this call.
    private void Activate()
    {
        _flushed = true;
        try
        {
            var dir = ConnectorLogPaths.ResolveLogDirectory();
            Directory.CreateDirectory(dir);
            // Process.GetCurrentProcess().Id rather than Environment.ProcessId (.NET 5+ only) -
            // this project multi-targets down to net462.
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            LogFilePath = Path.Combine(dir, $"reqnroll-{_ide}-{_role}-{DateTime.UtcNow:yyyyMMdd}-{pid}.log");
        }
        catch
        {
            // Best-effort — file logging must never break discovery.
            LogFilePath = null;
            return;
        }

        foreach (var buffered in _buffer)
            WriteLine(buffered);
        _buffer.Clear();
    }

    // Caller must hold _writeLock.
    private void WriteLine(string line)
    {
        if (LogFilePath is null) return;

        try
        {
            File.AppendAllText(LogFilePath, line, Encoding.UTF8);
        }
        catch
        {
            // Best-effort — file logging must never break discovery.
        }
    }
}
