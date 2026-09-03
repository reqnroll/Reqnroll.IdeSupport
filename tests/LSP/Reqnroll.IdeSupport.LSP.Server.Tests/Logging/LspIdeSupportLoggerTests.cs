using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Logging;

/// <summary>
/// Issue #568: <see cref="LspIdeSupportLogger"/> has zero test references anywhere in the
/// codebase. It composes a real <see cref="Common.Logging.SynchronousFileLogger"/> that writes to
/// the developer's actual log folder (already exercised the same way by
/// <c>SynchronousFileLoggerTests</c> in the Common test project), so these tests avoid asserting
/// on that file's exact path/content and instead cover the composition behaviour the class itself
/// is responsible for.
/// </summary>
public class LspIdeSupportLoggerTests : IDisposable
{
    private readonly string? _originalReqnrollVsDebug;

    public LspIdeSupportLoggerTests()
    {
        // Isolates REQNROLLVS_DEBUG per test: some dev machines have it set ambiently, which
        // would otherwise leak into the "at least as verbose as requested" assertions below.
        _originalReqnrollVsDebug = Environment.GetEnvironmentVariable("REQNROLLVS_DEBUG");
        Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("REQNROLLVS_DEBUG", _originalReqnrollVsDebug);

    [Theory]
    [InlineData(TraceLevel.Off)]
    [InlineData(TraceLevel.Error)]
    [InlineData(TraceLevel.Warning)]
    [InlineData(TraceLevel.Info)]
    [InlineData(TraceLevel.Verbose)]
    public void Level_is_never_less_verbose_than_the_requested_client_log_level(TraceLevel requested)
    {
        // The composite logger's Level is the MAX of its composed loggers (the debug-output
        // logger defaults to Verbose in a Debug build regardless of --log-level), so the only
        // invariant that holds across both Debug and Release configurations is "at least as
        // verbose as requested" — not exact equality.
        var sut = new LspIdeSupportLogger(new ClientIdeContext("visualstudio", requested));

        sut.Level.Should().BeOneOf(TraceLevelsAtOrAbove(requested));
    }

    private static TraceLevel[] TraceLevelsAtOrAbove(TraceLevel level) =>
        Enum.GetValues<TraceLevel>().Where(l => l >= level).ToArray();

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("vscode")]
    [InlineData("some-unknown-ide")]
    [InlineData(null)]
    public void Constructing_does_not_throw_for_any_client_ide_value(string? ide)
    {
        var act = () => new LspIdeSupportLogger(new ClientIdeContext(ide));

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_does_not_throw_for_a_message_at_every_trace_level()
    {
        var sut = new LspIdeSupportLogger(new ClientIdeContext("visualstudio", TraceLevel.Verbose));

        foreach (TraceLevel level in Enum.GetValues<TraceLevel>())
        {
            var act = () => sut.Log(new LogMessage(level, $"test message at {level}", nameof(Log_does_not_throw_for_a_message_at_every_trace_level)));
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Log_with_an_exception_does_not_throw()
    {
        var sut = new LspIdeSupportLogger(new ClientIdeContext("visualstudio", TraceLevel.Verbose));

        var act = () => sut.Log(new LogMessage(TraceLevel.Error, "boom",
            nameof(Log_with_an_exception_does_not_throw), new InvalidOperationException("inner")));

        act.Should().NotThrow();
    }
}
