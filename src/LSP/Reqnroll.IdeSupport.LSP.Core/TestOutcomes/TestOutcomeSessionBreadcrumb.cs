#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

/// <summary>
/// Writes a small breadcrumb file recording where this server's test-outcome TCP listener is bound,
/// so a test-runner-hosted reporter that cannot be handed the endpoint per-run (Microsoft.Testing.Platform
/// — issue #715, which has no per-run config injection channel the way VSTest's runsettings does) can
/// discover it instead: one file per LSP server process under
/// <c>&lt;Reqnroll application dir&gt;\test-outcomes\sessions\&lt;pid&gt;.json</c> (a sibling of the
/// <c>logs</c> subfolder, not inside it; issue #726), named by this process's own
/// PID so concurrently-running server instances (one per open VS/Rider/VS Code window) never collide.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Write</b> on every listener bind/rebind — overwrites the same file (keyed by this
///   process's PID), so a rebind naturally replaces a stale endpoint rather than leaving two entries.
///   Today's listener only ever binds once per process, but this stays correct if that changes.</item>
///   <item><b>Delete</b> on clean shutdown (<see cref="Dispose"/>) — a session that exits normally
///   leaves nothing behind for the next reporter to trip over.</item>
///   <item><b>Prune</b> dead-PID entries — covers the case a previous session's own clean-shutdown
///   delete never ran (crash, kill), so the directory doesn't grow unbounded across many sessions
///   (issue #715 plan §7 risk #4). <see cref="Write"/> prunes before writing, so a reporter never sees
///   a bind followed by leftover clutter from earlier sessions; <see cref="PruneStaleEntries"/> is also
///   exposed directly so it is independently testable without a real bind.</item>
/// </list>
/// Every failure is logged and swallowed: this is a discovery convenience for a reporter that already
/// degrades gracefully with nothing to connect to, never a reason to fail the server itself.
/// </remarks>
public sealed class TestOutcomeSessionBreadcrumb : IDisposable
{
    private readonly string _sessionsDirectory;
    private readonly int _pid;
    private readonly Func<int, bool> _isProcessRunning;
    private readonly IIdeSupportLogger _logger;
    private readonly DateTime _startedUtc;
    private readonly object _gate = new();
    private bool _written;

    /// <summary>DI entry point.</summary>
    public TestOutcomeSessionBreadcrumb(IIdeSupportLogger logger)
        : this(ResolveDefaultSessionsDirectory(logger), CurrentProcessId(), IsProcessRunning, logger)
    {
    }

    /// <summary>Test seam: explicit sessions directory, PID, and a liveness check that never spawns a real process lookup.</summary>
    internal TestOutcomeSessionBreadcrumb(string sessionsDirectory, int pid, Func<int, bool> isProcessRunning, IIdeSupportLogger logger)
    {
        _sessionsDirectory = sessionsDirectory;
        _pid = pid;
        _isProcessRunning = isProcessRunning;
        _logger = logger;
        _startedUtc = DateTime.UtcNow;
    }

    private string FilePath => Path.Combine(_sessionsDirectory, $"{_pid}.json");

    /// <summary>
    /// Uses <see cref="ReqnrollLogPaths.ResolveApplicationDirectory"/>, not <c>ResolveLogDirectory</c>
    /// — these breadcrumbs are discovery state for the MTP reporter, not logs (issue #726), and this
    /// location must keep agreeing with the reporter side's independent
    /// <c>Reqnroll.IdeSupport.TestReporter.MTP.SessionsDirectory.Resolve</c>, which was never changed
    /// by that issue. Mirrors <see cref="Reqnroll.IdeSupport.Common.Logging.TelemetryDebugLog.DefaultPath"/>'s
    /// pattern: never let path resolution take the pipeline down.
    /// </summary>
    private static string ResolveDefaultSessionsDirectory(IIdeSupportLogger logger)
    {
        try
        {
            return Path.Combine(ReqnrollLogPaths.ResolveApplicationDirectory(), "test-outcomes", "sessions");
        }
        catch (Exception ex)
        {
            logger.LogException(ex, $"{nameof(TestOutcomeSessionBreadcrumb)}: could not resolve the Reqnroll application directory; falling back to the temp directory");
            return Path.Combine(Path.GetTempPath(), "reqnroll-test-outcomes-sessions");
        }
    }

    private static int CurrentProcessId()
    {
        using var self = Process.GetCurrentProcess();
        return self.Id;
    }

    /// <summary>
    /// True if <paramref name="pid"/> is confirmed running, false only when confirmed gone
    /// (<see cref="ArgumentException"/> — no such process). Any other failure (e.g. access denied)
    /// is treated as "can't tell, assume running": wrongly skipping a prune leaves one extra file
    /// around; wrongly pruning could delete a live session's only breadcrumb.
    /// </summary>
    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Deletes breadcrumb files for sessions no longer running. Safe to call repeatedly.</summary>
    public void PruneStaleEntries()
    {
        try
        {
            if (!Directory.Exists(_sessionsDirectory)) return;
            var pruned = 0;
            foreach (var file in Directory.EnumerateFiles(_sessionsDirectory, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name, out var pid) || !_isProcessRunning(pid))
                {
                    try
                    {
                        File.Delete(file);
                        pruned++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"{nameof(TestOutcomeSessionBreadcrumb)}: could not delete stale session file {file}: {ex.Message}");
                    }
                }
            }
            if (pruned > 0)
                _logger.LogVerbose($"{nameof(TestOutcomeSessionBreadcrumb)}: pruned {pruned} stale session file(s) from {_sessionsDirectory}");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomeSessionBreadcrumb)}: prune failed");
        }
    }

    /// <summary>Writes (or overwrites) this session's breadcrumb — call on every listener bind/rebind.</summary>
    public void Write(string endpoint, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint must not be empty.", nameof(endpoint));
        PruneStaleEntries();
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_sessionsDirectory);
                var json = new JObject
                {
                    ["endpoint"] = endpoint,
                    ["workspaceRoot"] = workspaceRoot,
                    ["lspServerPid"] = _pid,
                    ["startedUtc"] = _startedUtc,
                };
                File.WriteAllText(FilePath, json.ToString());
                _written = true;
            }
            _logger.LogVerbose($"{nameof(TestOutcomeSessionBreadcrumb)}: wrote {FilePath} (endpoint {endpoint})");
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomeSessionBreadcrumb)}: write failed");
        }
    }

    /// <summary>Deletes this session's breadcrumb — call on clean shutdown. A no-op if <see cref="Write"/> was never called.</summary>
    public void Delete()
    {
        try
        {
            lock (_gate)
            {
                if (!_written) return;
                if (File.Exists(FilePath)) File.Delete(FilePath);
                _written = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogException(ex, $"{nameof(TestOutcomeSessionBreadcrumb)}: delete failed");
        }
    }

    public void Dispose() => Delete();
}
