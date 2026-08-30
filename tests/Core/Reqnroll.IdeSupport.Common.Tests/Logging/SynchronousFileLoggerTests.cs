using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.Common.Tests.Logging;

// Isolates REQNROLLVS_DEBUG per test: some dev machines have it set ambiently (e.g. from the
// legacy Reqnroll.VisualStudio extension), which would otherwise leak into unrelated assertions.
public class SynchronousFileLoggerTests : IDisposable
{
    private readonly string? _originalReqnrollVsDebug;

    public SynchronousFileLoggerTests()
    {
        _originalReqnrollVsDebug = Environment.GetEnvironmentVariable("REQNROLLVS_DEBUG");
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", _originalReqnrollVsDebug);
    }

    [Fact]
    public void LogFilePath_includes_current_process_id()
    {
        var logger = new SynchronousFileLogger("test", $"pid-{Guid.NewGuid():N}");
        try
        {
            logger.LogFilePath.Should().Contain($"-{Process.GetCurrentProcess().Id}.log",
                "each process must write to its own log file so concurrent IDE instances don't share one");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Default_level_is_Warning()
    {
        var logger = new SynchronousFileLogger("test", $"default-{Guid.NewGuid():N}");
        try
        {
            logger.Level.Should().Be(TraceLevel.Warning);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Theory]
    [InlineData(TraceLevel.Off)]
    [InlineData(TraceLevel.Error)]
    [InlineData(TraceLevel.Warning)]
    [InlineData(TraceLevel.Info)]
    [InlineData(TraceLevel.Verbose)]
    public void Explicit_level_is_honored(TraceLevel level)
    {
        var logger = new SynchronousFileLogger("test", $"explicit-{Guid.NewGuid():N}", level);
        try
        {
            logger.Level.Should().Be(level);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Messages_above_the_configured_level_are_dropped()
    {
        var logger = new SynchronousFileLogger("test", $"filter-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Log(new LogMessage(TraceLevel.Info, "should be dropped",
                nameof(Messages_above_the_configured_level_are_dropped)));

            File.Exists(logger.LogFilePath).Should().BeFalse(
                "Info is below the Warning threshold and should never be written");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Messages_at_or_below_the_configured_level_are_written()
    {
        var logger = new SynchronousFileLogger("test", $"filter-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Log(new LogMessage(TraceLevel.Warning, "should be written",
                nameof(Messages_at_or_below_the_configured_level_are_written)));

            File.ReadAllText(logger.LogFilePath).Should().Contain("should be written");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void REQNROLLVS_DEBUG_truthy_value_forces_Verbose(string envValue)
    {
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", envValue);
        var logger = new SynchronousFileLogger("test", $"env-truthy-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Level.Should().Be(TraceLevel.Verbose);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void REQNROLLVS_DEBUG_TraceLevel_name_overrides_configured_level()
    {
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", "Error");
        var logger = new SynchronousFileLogger("test", $"env-named-{Guid.NewGuid():N}", TraceLevel.Verbose);
        try
        {
            logger.Level.Should().Be(TraceLevel.Error);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void REQNROLLVS_DEBUG_unparsable_value_leaves_configured_level_unchanged()
    {
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", "not-a-trace-level");
        var logger = new SynchronousFileLogger("test", $"env-invalid-{Guid.NewGuid():N}", TraceLevel.Warning);
        try
        {
            logger.Level.Should().Be(TraceLevel.Warning);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void REQNROLLVS_DEBUG_unset_leaves_configured_level_unchanged()
    {
        var logger = new SynchronousFileLogger("test", $"env-unset-{Guid.NewGuid():N}", TraceLevel.Info);
        try
        {
            logger.Level.Should().Be(TraceLevel.Info);
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    // Regression test for a real bug: SynchronousFileLogger.Log() calls WriteLogMessage directly on
    // the calling thread (unlike the base class's async path, which serializes through a single-
    // reader Channel), so with many threads logging concurrently onto one shared instance (as
    // happens with LspIdeSupportLogger's singleton and ~30 concurrently-firing LSP handlers),
    // unsynchronized File.AppendAllText calls could interleave/tear each other's writes — observed
    // live as a complete log line immediately followed by a stray fragment of another thread's line.
    // Without the lock in WriteLogMessage this test fails intermittently (torn/merged lines); with
    // it, every line must round-trip intact regardless of thread contention.
    [Fact]
    public void Concurrent_writers_never_interleave_or_tear_lines()
    {
        const int threadCount = 32;
        const int messagesPerThread = 50;
        var logger = new SynchronousFileLogger("test", $"concurrency-{Guid.NewGuid():N}", TraceLevel.Verbose);
        try
        {
            Parallel.For(0, threadCount, threadIndex =>
            {
                for (var i = 0; i < messagesPerThread; i++)
                {
                    // Padded and thread/index-tagged so a torn or merged line is detectable: it will
                    // either fail the per-line regex below or produce a duplicate/missing tag.
                    var payload = $"thread={threadIndex} index={i} ".PadRight(200, 'x');
                    logger.Log(new LogMessage(TraceLevel.Verbose, payload,
                        nameof(Concurrent_writers_never_interleave_or_tear_lines)));
                }
            });

            var lines = File.ReadAllLines(logger.LogFilePath)
                .Where(line => line.Length > 0)
                .ToList();

            lines.Should().HaveCount(threadCount * messagesPerThread,
                "every logged message should produce exactly one intact line, with none merged, split, or dropped");

            var seenTags = new HashSet<(int threadIndex, int index)>();
            var lineFormat = new Regex(
                @"^\S+, Verbose@\d+, Concurrent_writers_never_interleave_or_tear_lines: thread=(\d+) index=(\d+) x+$");
            foreach (var line in lines)
            {
                var match = lineFormat.Match(line);
                match.Success.Should().BeTrue($"line should be a single, intact, well-formed entry but was: {line}");
                var tag = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                seenTags.Add(tag).Should().BeTrue($"tag {tag} should appear exactly once, not merged into another line");
            }
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    private static void DeleteLogFile(SynchronousFileLogger logger)
    {
        try { File.Delete(logger.LogFilePath); } catch { /* best-effort cleanup */ }
    }
}
