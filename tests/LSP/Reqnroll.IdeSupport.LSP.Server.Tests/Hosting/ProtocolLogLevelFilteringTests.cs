using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

/// <summary>
/// Issue #660: OmniSharp's internal <c>LanguageServerLoggingManager</c> unconditionally overwrites
/// <see cref="LoggerFilterOptions.MinLevel"/> from the current LSP trace level, discarding whatever
/// <c>--protocol-log-level</c> configured via <c>SetMinimumLevel</c> in <see cref="Program.ConfigureServer"/>.
/// These tests exercise the real DI container OmniSharp builds (not a hand-rolled substitute) so a
/// regression in either our fix or a future OmniSharp upgrade that changes this behaviour is caught.
/// </summary>
public class ProtocolLogLevelFilteringTests
{
    private static readonly string ProtocolCategory = "OmniSharp.Extensions.LanguageServer.Shared.LspRequestRouter";

    private static IServiceProvider BuildServer(TraceLevel protocolLogLevel)
    {
        var (serverStream, _) = FullDuplexStream.CreatePair();
        var server = LanguageServer.PreInit(options =>
        {
            options.WithInput(serverStream).WithOutput(serverStream);
            Program.ConfigureServer(options, protocolLogLevel: protocolLogLevel);
        });
        return server.Services;
    }

    [Fact]
    public void Default_protocol_log_level_blocks_OmniSharp_internal_Info_noise_from_window_logMessage()
    {
        var logger = BuildServer(TraceLevel.Warning).GetRequiredService<ILoggerFactory>().CreateLogger(ProtocolCategory);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public void An_explicit_protocol_log_level_is_actually_honoured_and_not_reset_to_Warning()
    {
        // Before the fix, OmniSharp's LanguageServerLoggingManager.PostConfigure silently reset
        // MinLevel back to Warning here regardless of what --protocol-log-level requested.
        var logger = BuildServer(TraceLevel.Error).GetRequiredService<ILoggerFactory>().CreateLogger(ProtocolCategory);

        logger.IsEnabled(LogLevel.Warning).Should().BeFalse();
        logger.IsEnabled(LogLevel.Error).Should().BeTrue();
    }

    [Fact]
    public void A_more_verbose_protocol_log_level_is_also_honoured()
    {
        var logger = BuildServer(TraceLevel.Verbose).GetRequiredService<ILoggerFactory>().CreateLogger(ProtocolCategory);

        logger.IsEnabled(LogLevel.Trace).Should().BeTrue();
    }
}
