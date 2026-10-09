using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ProgramTests
{
    [Fact]
    public void ParseLogLevel_defaults_to_Warning_when_flag_is_absent()
    {
        Program.ParseLogLevel(new[] { "--ide", "visualstudio" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseLogLevel_defaults_to_Warning_for_no_args()
    {
        Program.ParseLogLevel(Array.Empty<string>()).Should().Be(TraceLevel.Warning);
    }

    [Theory]
    [InlineData("Off", TraceLevel.Off)]
    [InlineData("error", TraceLevel.Error)]
    [InlineData("WARNING", TraceLevel.Warning)]
    [InlineData("Info", TraceLevel.Info)]
    [InlineData("verbose", TraceLevel.Verbose)]
    public void ParseLogLevel_parses_case_insensitively(string arg, TraceLevel expected)
    {
        Program.ParseLogLevel(new[] { "--ide", "vscode", "--log-level", arg }).Should().Be(expected);
    }

    [Fact]
    public void ParseLogLevel_defaults_to_Warning_for_an_unrecognized_value()
    {
        Program.ParseLogLevel(new[] { "--log-level", "not-a-level" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseProtocolLogLevel_defaults_to_Warning_when_flag_is_absent()
    {
        Program.ParseProtocolLogLevel(new[] { "--ide", "visualstudio" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseProtocolLogLevel_defaults_to_Warning_for_no_args()
    {
        Program.ParseProtocolLogLevel(Array.Empty<string>()).Should().Be(TraceLevel.Warning);
    }

    [Theory]
    [InlineData("Off", TraceLevel.Off)]
    [InlineData("error", TraceLevel.Error)]
    [InlineData("WARNING", TraceLevel.Warning)]
    [InlineData("Info", TraceLevel.Info)]
    [InlineData("verbose", TraceLevel.Verbose)]
    public void ParseProtocolLogLevel_parses_case_insensitively(string arg, TraceLevel expected)
    {
        Program.ParseProtocolLogLevel(new[] { "--protocol-log-level", arg }).Should().Be(expected);
    }

    [Fact]
    public void ParseProtocolLogLevel_defaults_to_Warning_for_an_unrecognized_value()
    {
        Program.ParseProtocolLogLevel(new[] { "--protocol-log-level", "not-a-level" }).Should().Be(TraceLevel.Warning);
    }

    [Fact]
    public void ParseProtocolLogLevel_is_independent_of_log_level()
    {
        var args = new[] { "--log-level", "Off", "--protocol-log-level", "Verbose" };

        Program.ParseLogLevel(args).Should().Be(TraceLevel.Off);
        Program.ParseProtocolLogLevel(args).Should().Be(TraceLevel.Verbose);
    }

    [Fact]
    public void ParseArg_returns_the_value_following_the_flag()
    {
        Program.ParseArg(new[] { "--ide", "visualstudio", "--log-level", "Verbose" }, "--ide")
            .Should().Be("visualstudio");
    }

    [Fact]
    public void ParseArg_returns_null_when_the_flag_is_absent()
    {
        Program.ParseArg(new[] { "--ide", "vscode" }, "--log-level").Should().BeNull();
    }

    [Fact]
    public void ParseTraceLevel_defaults_to_Off_when_flag_is_absent()
    {
        Program.ParseTraceLevel(new[] { "--ide", "visualstudio" }).Should().Be(InitializeTrace.Off);
    }

    [Fact]
    public void ParseTraceLevel_defaults_to_Off_for_no_args()
    {
        Program.ParseTraceLevel(Array.Empty<string>()).Should().Be(InitializeTrace.Off);
    }

    [Theory]
    [InlineData("Off", InitializeTrace.Off)]
    [InlineData("MESSAGES", InitializeTrace.Messages)]
    [InlineData("verbose", InitializeTrace.Verbose)]
    public void ParseTraceLevel_parses_case_insensitively(string arg, InitializeTrace expected)
    {
        Program.ParseTraceLevel(new[] { "--trace", arg }).Should().Be(expected);
    }

    [Fact]
    public void ParseTraceLevel_defaults_to_Off_for_an_unrecognized_value()
    {
        Program.ParseTraceLevel(new[] { "--trace", "not-a-level" }).Should().Be(InitializeTrace.Off);
    }

    [Theory]
    [InlineData(InitializeTrace.Off, InitializeTrace.Off, InitializeTrace.Off)]
    [InlineData(InitializeTrace.Verbose, InitializeTrace.Off, InitializeTrace.Verbose)]
    [InlineData(InitializeTrace.Off, InitializeTrace.Messages, InitializeTrace.Messages)]
    [InlineData(InitializeTrace.Verbose, InitializeTrace.Messages, InitializeTrace.Messages)]
    public void ResolveInitialTrace_prefers_the_requested_level_unless_it_is_Off(
        InitializeTrace current, InitializeTrace requested, InitializeTrace expected)
    {
        Program.ResolveInitialTrace(current, requested).Should().Be(expected);
    }

    [Theory]
    [InlineData(TraceLevel.Off, LogLevel.None)]
    [InlineData(TraceLevel.Error, LogLevel.Error)]
    [InlineData(TraceLevel.Warning, LogLevel.Warning)]
    [InlineData(TraceLevel.Info, LogLevel.Information)]
    [InlineData(TraceLevel.Verbose, LogLevel.Trace)]
    public void ToLogLevel_maps_each_TraceLevel_to_the_matching_LogLevel(TraceLevel traceLevel, LogLevel expected)
    {
        Program.ToLogLevel(traceLevel).Should().Be(expected);
    }

    [Fact]
    public void GetServerVersion_returns_the_assembly_informational_version()
    {
        var expected = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Program.GetServerVersion().Should().Be(expected).And.NotBeNullOrEmpty();
    }

    [Fact]
    public void GetServerVersion_matches_the_MainVersion_dot_BuildNumber_pattern()
    {
        // build/Version.props stamps VersionPrefix as <ReqnrollMainVersion>.<ReqnrollBuildNumber>
        // (e.g. "2026.1.99999"), with an optional "-<suffix>" and a "+<git-sha>" appended by the SDK.
        Program.GetServerVersion().Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?(\+[0-9a-f]+)?$");
    }
}
