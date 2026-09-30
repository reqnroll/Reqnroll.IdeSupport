#nullable enable

using System;
using System.IO;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Benchmarks.Harness;

/// <summary>
/// Issue #714's hermeticity prerequisite, benchmark side: points the server's outcome store at a
/// per-run temp file via <see cref="TestOutcomePersistence.FilePathEnvironmentVariable"/>, so no
/// scenario in the suite can read or mutate the developer's real 30-day
/// <c>%LOCALAPPDATA%\Reqnroll\test-outcomes.json</c> (which every <c>getOutcome</c>/<c>registerRun</c>
/// scenario would otherwise measure <em>and</em> overwrite).
/// </summary>
/// <remarks>
/// The environment variable is process-wide, which is exactly why this covers both transports: the
/// in-process server reads it directly, and the out-of-process server (spawned with
/// <c>UseShellExecute = false</c>) inherits the benchmark process's environment. Set it before the
/// first server starts — the path is resolved once, when <see cref="TestOutcomePersistence"/> is
/// constructed (lazily, on the session's first outcome request).
/// </remarks>
public sealed class TestOutcomePersistenceRedirect : IDisposable
{
    private readonly string _directory;

    private TestOutcomePersistenceRedirect(string directory, string filePath)
    {
        _directory = directory;
        FilePath = filePath;
    }

    /// <summary>
    /// Where the persistence-scale fixture (scenario E) puts the container files its entries point
    /// at — inside the per-run directory, so it is cleaned up with everything else.
    /// </summary>
    public string ContainerDirectory => Path.Combine(_directory, "containers");

    /// <summary>A path for another state file inside the per-run directory (see <see cref="UseStore"/>).</summary>
    public string ForFile(string fileName) => Path.Combine(_directory, fileName);

    /// <summary>
    /// Points the server at a <em>different</em> store file for the lifetime of the returned scope, then
    /// restores the previous value. Needed because the persistence-scale scenarios own a deliberately
    /// large <b>fixture</b> file: sharing the per-run file the live store saves to lets a listener
    /// <c>Save</c> — which can fire at any moment, on a connection closing — overwrite that fixture
    /// between writing it and reading it back. Scoping the redirected path keeps the fixture and the
    /// live store in separate files, so neither can clobber the other.
    /// </summary>
    /// <remarks>
    /// Safe to swap only after the live store's persistence has been constructed (it resolves its path
    /// once, in the constructor) — which is true by the time the batch scenarios run, since the outcome
    /// scenarios before them have already forced it. The value is restored on dispose, so anything
    /// resolved afterwards — the contention scenario's connections, for instance — goes back to the
    /// run's own file.
    /// </remarks>
    public IDisposable UseStore(string filePath)
    {
        var previous = Environment.GetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable);
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, filePath);
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() =>
            Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, _previous);
    }

    /// <summary>Where the redirected store persists — the file the load/save scenarios measure against.</summary>
    public string FilePath { get; }

    /// <summary>Creates the per-run directory, points the server at a file inside it, and returns the redirect.</summary>
    public static TestOutcomePersistenceRedirect Begin(string? filePath = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "reqnroll-benchmark-outcomes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        filePath ??= Path.Combine(directory, "test-outcomes.json");
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, filePath);
        return new TestOutcomePersistenceRedirect(directory, filePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestOutcomePersistence.FilePathEnvironmentVariable, null);
        try { Directory.Delete(_directory, recursive: true); } catch (Exception) { }
    }
}