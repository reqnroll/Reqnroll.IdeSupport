using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Logging;

public class FileLoggerTests
{
    [Fact]
    public void Info_only_logs_never_touch_disk_by_default()
    {
        // The core of issue #637: a routine, uneventful discovery run leaves no file behind.
        var logger = new FileLogger(alwaysWrite: false, "test-ide", $"quiet-{Guid.NewGuid():N}");

        logger.Log(new Log(LogLevel.Info, "loading assembly"));
        logger.Log(new Log(LogLevel.Info, "discovery complete"));

        logger.LogFilePath.Should().BeNull("nothing worth persisting happened, so no path should ever be resolved");
    }

    [Fact]
    public void An_error_flushes_the_buffered_context_that_preceded_it()
    {
        var role = $"flush-{Guid.NewGuid():N}";
        var logger = new FileLogger(alwaysWrite: false, "test-ide", role);
        try
        {
            logger.Log(new Log(LogLevel.Info, "first"));
            logger.Log(new Log(LogLevel.Info, "second"));
            logger.Log(new Log(LogLevel.Error, "boom", new InvalidOperationException("bad")));

            logger.LogFilePath.Should().NotBeNull();
            logger.LogFilePath!.Should().Contain($"reqnroll-test-ide-{role}-");
            // The exception detail itself spans an extra indented physical line (matching
            // SynchronousFileLogger's convention), so this checks content, not a physical line count.
            var content = File.ReadAllText(logger.LogFilePath);
            content.Should().Contain("first", "the buffered context leading up to the error should survive")
                .And.Contain("second")
                .And.Contain("boom").And.Contain("InvalidOperationException").And.Contain("bad");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Lines_logged_after_the_flushing_error_are_written_immediately()
    {
        var logger = new FileLogger(alwaysWrite: false, "test-ide", $"after-error-{Guid.NewGuid():N}");
        try
        {
            logger.Log(new Log(LogLevel.Error, "boom"));
            logger.Log(new Log(LogLevel.Info, "cleanup step"));

            var lines = File.ReadAllLines(logger.LogFilePath!).Where(l => l.Length > 0).ToList();
            lines.Should().HaveCount(2);
            lines[1].Should().Contain("cleanup step");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void AlwaysWrite_true_writes_every_line_immediately_even_without_an_error()
    {
        // The Connector's --debug flag maps to alwaysWrite: true (Program.cs).
        var logger = new FileLogger(alwaysWrite: true, "test-ide", $"always-{Guid.NewGuid():N}");
        try
        {
            logger.Log(new Log(LogLevel.Info, "hello"));

            logger.LogFilePath.Should().NotBeNull();
            var line = File.ReadAllText(logger.LogFilePath!);
            line.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Info \] hello");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void LogFilePath_includes_the_ide_role_date_and_process_id_once_activated()
    {
        var logger = new FileLogger(alwaysWrite: true, "test-ide", "test-role");
        try
        {
            logger.Log(new Log(LogLevel.Info, "activate"));

            logger.LogFilePath.Should().NotBeNull();
            logger.LogFilePath!.Should().Contain($"reqnroll-test-ide-test-role-{DateTime.UtcNow:yyyyMMdd}-")
                .And.EndWith(".log");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    // Regression coverage for the write lock: File.AppendAllText is not inherently safe against
    // concurrent callers on the same path, so Log() serializes writes itself (mirrors
    // SynchronousFileLoggerTests.Concurrent_writers_never_interleave_or_tear_lines). Uses
    // alwaysWrite so every call takes the live-write path this test is actually exercising.
    [Fact]
    public void Concurrent_writers_never_interleave_or_tear_lines()
    {
        const int threadCount = 16;
        const int messagesPerThread = 25;
        var logger = new FileLogger(alwaysWrite: true, "test-ide", $"concurrency-{Guid.NewGuid():N}");
        try
        {
            Parallel.For(0, threadCount, threadIndex =>
            {
                for (var i = 0; i < messagesPerThread; i++)
                {
                    var payload = $"thread={threadIndex} index={i} ".PadRight(150, 'x');
                    logger.Log(new Log(LogLevel.Info, payload));
                }
            });

            var lines = File.ReadAllLines(logger.LogFilePath!).Where(l => l.Length > 0).ToList();
            lines.Should().HaveCount(threadCount * messagesPerThread);

            var seenTags = new HashSet<(int threadIndex, int index)>();
            foreach (var line in lines)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"thread=(\d+) index=(\d+) x+$");
                match.Success.Should().BeTrue($"line should be a single, intact entry but was: {line}");
                var tag = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                seenTags.Add(tag).Should().BeTrue($"tag {tag} should appear exactly once, not merged into another line");
            }
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    private static void DeleteLogFile(FileLogger logger)
    {
        try { if (logger.LogFilePath is not null) File.Delete(logger.LogFilePath); } catch { /* best-effort cleanup */ }
    }
}
