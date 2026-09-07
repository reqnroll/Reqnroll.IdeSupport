#nullable disable

using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>Runs external command-line processes and captures their exit code, output, and errors.</summary>
public static class ProcessHelper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Starts the given executable with the supplied arguments and waits for it to exit, capturing its output.</summary>
    /// <param name="throwException">When true, exceptions from starting or running the process are rethrown instead of being captured in the result.</param>
    /// <param name="logger">
    /// Optional sink for best-effort failures that don't affect the returned result (e.g. a failed
    /// attempt to kill an already-timed-out process) — falls back to <see cref="Debug.WriteLine(object)"/>
    /// when omitted, which is invisible outside a debugger and left no trace at all before (issue #626).
    /// </param>
    /// <returns>The captured exit code, standard output, and standard error, or a failure result if <paramref name="throwException"/> is false and an error occurred.</returns>
    public static RunProcessResult RunProcess(string workingDirectory, string executablePath,
        IEnumerable<string> arguments, TimeSpan? timeout = null, bool throwException = false, Encoding encoding = null,
        IIdeSupportLogger logger = null)
    {
        var parameters = string.Join(" ", arguments.Select(GetSafeArgument));
        try
        {
            return RunProcessInternal(workingDirectory, executablePath, parameters, timeout, encoding, logger);
        }
        catch (Exception ex)
        {
            if (throwException)
                throw;

            return new RunProcessResult(-1, "", ex.Message, executablePath, parameters, workingDirectory);
        }
    }

    private static RunProcessResult RunProcessInternal(string workingDirectory, string executablePath,
        string parameters, TimeSpan? timeout, Encoding encoding, IIdeSupportLogger logger)
    {
        timeout = timeout ?? DefaultTimeout;

        if (workingDirectory == null || !Directory.Exists(workingDirectory))
            throw new IdeSupportConfigurationException($"Unable to find directory: {workingDirectory}");

        // A bare command name (no directory component, e.g. "dotnet") is meant to be resolved by
        // the OS via PATH when the process launches — File.Exists can't check that, so only
        // pre-validate paths that look like real file paths. If a bare command genuinely isn't
        // resolvable, Process.Start below throws and that failure is surfaced normally.
        var looksLikeFilePath = executablePath != null && !string.IsNullOrEmpty(Path.GetDirectoryName(executablePath));
        if (executablePath == null || (looksLikeFilePath && !File.Exists(executablePath)))
            throw new IdeSupportConfigurationException($"Unable to find process: {executablePath}");

        ProcessStartInfo psi = new ProcessStartInfo(executablePath, parameters)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory
        };

        if (encoding != null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }

        using var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        var consoleOutBuilder = new StringBuilder();
        var consoleErrorBuilder = new StringBuilder();

        using (var outputCollector = new ProcessOutputCollector(process, consoleOutBuilder, consoleErrorBuilder, logger))
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start process");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeOutInMilliseconds = Convert.ToInt32(timeout.Value.TotalMilliseconds);
            if (!process.WaitForExit(timeOutInMilliseconds) ||
                !outputCollector.OutputWaitHandle.WaitOne(timeOutInMilliseconds) ||
                !outputCollector.ErrorWaitHandle.WaitOne(timeOutInMilliseconds))
                throw new TimeoutException(
                    $"Process {psi.FileName} {psi.Arguments} took longer than {timeout.Value.TotalMinutes} min to complete");
        }

        return new RunProcessResult(process.ExitCode, consoleOutBuilder.ToString(), consoleErrorBuilder.ToString(),
            psi.FileName, psi.Arguments, psi.WorkingDirectory);
    }

    private static string GetSafeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (!arg.Contains(' ') || arg.StartsWith("\""))
            return arg;

        //source: https://stackoverflow.com/a/12364234/26530
        string value = Regex.Replace(arg, @"(\\*)" + "\"", @"$1\$0");
        value = Regex.Replace(value, @"^(.*\s.*?)(\\*)$", "\"$1$2$2\"", RegexOptions.Singleline);
        return value;
    }

    /// <summary>The captured outcome of running an external process via <see cref="ProcessHelper.RunProcess"/>.</summary>
    public class RunProcessResult
    {
        /// <summary>Creates a result describing how a process invocation completed.</summary>
        public RunProcessResult(int exitCode, string standardOut, string standardError, string executablePath,
            string arguments, string workingDirectory)
        {
            ExitCode = exitCode;
            StandardOut = standardOut ?? "";
            StandardError = standardError;
            ExecutablePath = executablePath;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
        }

        /// <summary>The process's exit code.</summary>
        public int ExitCode { get; }
        /// <summary>Everything the process wrote to standard output.</summary>
        public string StandardOut { get; }
        /// <summary>Everything the process wrote to standard error.</summary>
        public string StandardError { get; }
        /// <summary>Path of the executable that was run.</summary>
        public string ExecutablePath { get; }
        /// <summary>Command-line arguments passed to the executable.</summary>
        public string Arguments { get; }
        /// <summary>Working directory the process was launched from.</summary>
        public string WorkingDirectory { get; }

        /// <summary>True if the process wrote anything to standard error.</summary>
        public bool HasErrors => !string.IsNullOrWhiteSpace(StandardError);

        /// <summary>The working directory, executable, and arguments formatted as a single command-line string.</summary>
        public string CommandLine =>
            $"{WorkingDirectory}> {ExecutablePath} {Arguments}";
    }

    private class ProcessOutputCollector : IDisposable
    {
        private readonly Process _process;
        private readonly IIdeSupportLogger _logger;

        public ProcessOutputCollector(Process process, StringBuilder consoleOutBuilder,
            StringBuilder consoleErrorBuilder, IIdeSupportLogger logger)
        {
            _process = process;
            _logger = logger;
            ConsoleOutBuilder = consoleOutBuilder;
            ConsoleErrorBuilder = consoleErrorBuilder;
            OutputWaitHandle = new AutoResetEvent(false);
            ErrorWaitHandle = new AutoResetEvent(false);

            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
        }

        public AutoResetEvent OutputWaitHandle { get; }
        public AutoResetEvent ErrorWaitHandle { get; }
        public StringBuilder ConsoleOutBuilder { get; }
        public StringBuilder ConsoleErrorBuilder { get; }

        public void Dispose()
        {
            _process.OutputDataReceived -= ProcessOnOutputDataReceived;
            _process.ErrorDataReceived -= ProcessOnErrorDataReceived;

            OutputWaitHandle.Dispose();
            ErrorWaitHandle.Dispose();

            if (!_process.HasExited)
                KillProcess();
        }

        private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                OutputWaitHandle.Set();
            else
                ConsoleOutBuilder.AppendLine(e.Data);
        }

        private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                // ReSharper disable once AccessToDisposedClosure
                ErrorWaitHandle.Set();
            else
                ConsoleErrorBuilder.AppendLine(e.Data);
        }

        private void KillProcess()
        {
            try
            {
                _process.Kill();
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.LogWarning($"Failed to kill timed-out process: {ex.Message}");
                else
                    Debug.WriteLine(ex);
            }
        }
    }
}
