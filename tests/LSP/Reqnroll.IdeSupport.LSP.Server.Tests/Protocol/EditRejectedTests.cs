using OmniSharp.Extensions.JsonRpc.Server;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Protocol;

public class EditRejectedTests
{
    [Fact]
    public void Code_is_the_LSP_RequestFailed_code()
    {
        EditRejected.Code.Should().Be(-32803);
        EditRejected.Code.Should().Be(ErrorCodes.RequestFailed);
    }

    [Fact]
    public void Error_carries_the_code_message_and_scope()
    {
        var error = EditRejected.Error("Parameter count mismatch", "rename");

        error.Code.Should().Be(-32803);
        error.Message.Should().Be("Parameter count mismatch");
        error.Error.Should().Be("rename");
    }

    [Fact]
    public void Error_without_a_scope_has_no_data()
    {
        var error = EditRejected.Error("Nope");

        error.Code.Should().Be(EditRejected.Code);
        error.Message.Should().Be("Nope");
        error.Error.Should().BeNull();
    }
}
