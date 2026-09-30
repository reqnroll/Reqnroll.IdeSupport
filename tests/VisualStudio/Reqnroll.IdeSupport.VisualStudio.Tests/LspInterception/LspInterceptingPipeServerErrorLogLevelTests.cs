using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Covers the level a server-rejected request is logged at (issue #800 follow-up): ContentModified
/// is routine and retried, so it must not surface as a Warning in the Output pane.
/// </summary>
public class LspInterceptingPipeServerErrorLogLevelTests
{
    [Fact]
    public void ContentModified_is_logged_at_Debug()
    {
        var error = new JObject { ["code"] = LspContentModifiedException.ErrorCode, ["message"] = "Content Modified" };

        LspInterceptingPipe.ServerErrorLogLevel(error).Should().Be(LogLevel.Debug);
    }

    [Theory]
    [InlineData(-32603)] // InternalError
    [InlineData(-32601)] // MethodNotFound
    [InlineData(-32800)] // RequestCancelled
    public void Other_server_errors_stay_Warnings(int code)
    {
        var error = new JObject { ["code"] = code, ["message"] = "boom" };

        LspInterceptingPipe.ServerErrorLogLevel(error).Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void An_error_without_a_numeric_code_stays_a_Warning()
    {
        LspInterceptingPipe.ServerErrorLogLevel(new JObject { ["message"] = "boom" }).Should().Be(LogLevel.Warning);
        LspInterceptingPipe.ServerErrorLogLevel(new JObject { ["code"] = "-32801" }).Should().Be(LogLevel.Warning);
    }
}
