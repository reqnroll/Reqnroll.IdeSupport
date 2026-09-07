using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Logging;

public class FileLoggerTests
{
    [Fact]
    public void LogFilePath_includes_the_ide_role_date_and_process_id()
    {
        var logger = new FileLogger("test-ide", "test-role");
        try
        {
            logger.LogFilePath.Should().NotBeNull();
            logger.LogFilePath!.Should().Contain($"reqnroll-test-ide-test-role-{DateTime.UtcNow:yyyyMMdd}-")
                .And.EndWith(".log");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Log_writes_a_UTC_timestamped_padded_level_line()
    {
        var logger = new FileLogger("test-ide", $"format-{Guid.NewGuid():N}");
        try
        {
            logger.Log(new Log(LogLevel.Info, "hello"));

            var line = File.ReadAllText(logger.LogFilePath!);
            line.Should().MatchRegex(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[Info \] hello");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Log_appends_an_indented_exception_block_when_present()
    {
        var logger = new FileLogger("test-ide", $"exception-{Guid.NewGuid():N}");
        try
        {
            logger.Log(new Log(LogLevel.Error, "boom", new InvalidOperationException("bad")));

            var content = File.ReadAllText(logger.LogFilePath!);
            content.Should().Contain("boom").And.Contain("InvalidOperationException").And.Contain("bad");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    [Fact]
    public void Multiple_log_calls_each_produce_their_own_line()
    {
        var logger = new FileLogger("test-ide", $"multi-{Guid.NewGuid():N}");
        try
        {
            logger.Log(new Log(LogLevel.Info, "first"));
            logger.Log(new Log(LogLevel.Info, "second"));

            var lines = File.ReadAllLines(logger.LogFilePath!).Where(l => l.Length > 0).ToList();
            lines.Should().HaveCount(2);
            lines[0].Should().Contain("first");
            lines[1].Should().Contain("second");
        }
        finally
        {
            DeleteLogFile(logger);
        }
    }

    // Regression coverage for the write lock: File.AppendAllText is not inherently safe against
    // concurrent callers on the same path, so Log() serializes writes itself (mirrors
    // SynchronousFileLoggerTests.Concurrent_writers_never_interleave_or_tear_lines).
    [Fact]
    public void Concurrent_writers_never_interleave_or_tear_lines()
    {
        const int threadCount = 16;
        const int messagesPerThread = 25;
        var logger = new FileLogger("test-ide", $"concurrency-{Guid.NewGuid():N}");
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
