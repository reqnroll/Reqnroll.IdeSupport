using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// Covers the client side of the server's "edit rejected" contract (issue #1132): only a
/// <c>-32803</c> error with a message is a user-facing rejection; everything else — including
/// <c>ContentModified</c> — must never reach the modal dialog.
/// </summary>
public class EditRejectionTests
{
    private static JObject Error(object? code, string? message) =>
        new() { ["code"] = code is null ? null : JToken.FromObject(code), ["message"] = message };

    [Fact]
    public void Error_code_is_the_LSP_RequestFailed_code()
    {
        EditRejectedException.ErrorCode.Should().Be(-32803);
    }

    [Fact]
    public void A_RequestFailed_error_becomes_a_rejection_carrying_the_server_message()
    {
        var rejection = EditRejection.TryCreateException(Error(-32803, "Parameter count mismatch"));

        rejection.Should().NotBeNull();
        rejection!.Message.Should().Be("Parameter count mismatch");
    }

    [Fact]
    public void A_null_error_is_not_a_rejection()
    {
        EditRejection.TryCreateException(null).Should().BeNull();
    }

    [Theory]
    [InlineData(-32801)]   // ContentModified: routine, retryable
    [InlineData(-32603)]   // InternalError
    [InlineData(-32800)]   // RequestCancelled
    [InlineData(0)]
    public void Any_other_error_code_is_not_a_rejection(int code)
    {
        EditRejection.TryCreateException(Error(code, "something the user should not see"))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_RequestFailed_error_without_a_message_is_not_shown(string? message)
    {
        EditRejection.TryCreateException(Error(-32803, message)).Should().BeNull();
    }

    [Fact]
    public void A_non_integer_code_is_not_a_rejection()
    {
        EditRejection.TryCreateException(Error("-32803", "msg")).Should().BeNull();
    }

    [Fact]
    public void An_error_with_no_code_is_not_a_rejection()
    {
        EditRejection.TryCreateException(Error(null, "msg")).Should().BeNull();
    }

    [Fact]
    public async Task ReportAsync_shows_the_message_through_the_modal_seam()
    {
        string? shown = null;

        await EditRejection.ReportAsync(
            NullLogger.Instance, "RenameStepCommand", "Parameter count mismatch", CancellationToken.None,
            (message, _) =>
            {
                shown = message;
                return Task.CompletedTask;
            });

        shown.Should().Be("Parameter count mismatch");
    }
}
