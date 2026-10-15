using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.RenameStep;

/// <summary>
/// Issue #1019: a rename that timed out, was cancelled or hit a dead server must not be reported as
/// "Step renamed successfully", and a failed target lookup must not read as "no step definition found".
/// </summary>
public class RenameStepServiceFailureTests
{
    private const string FileUri = "file:///C:/w/Steps.cs";
    private const int ContentModified = -32801;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    private static RenameStepService CreateSut(ScriptedServerPipe server) =>
        new(server.Pipe, NullLogger<RenameStepService>.Instance);

    [Fact]
    public async Task A_rename_that_times_out_throws_instead_of_completing_normally()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        var ex = await Assert.ThrowsAsync<RenameFailedException>(
            () => CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", CancellationToken.None));

        ex.Message.Should().Contain("did not complete").And.Contain("did not respond in time");
    }

    [Fact]
    public async Task A_rename_to_a_terminated_server_throws()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.Pipe.MarkServerTerminated("test");

        var ex = await Assert.ThrowsAsync<RenameFailedException>(
            () => CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", CancellationToken.None));

        ex.Message.Should().Contain("not available");
    }

    [Fact]
    public async Task A_cancelled_rename_throws_OperationCanceledException()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", cts.Token));
    }

    [Fact]
    public async Task A_server_rejection_still_carries_the_servers_reason()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32803, "Parameter count mismatch");

        var ex = await Assert.ThrowsAsync<EditRejectedException>(
            () => CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", CancellationToken.None));

        ex.Message.Should().Be("Parameter count mismatch");
    }

    [Fact]
    public async Task Any_other_server_error_is_a_generic_rename_failure()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32603, "internal detail");

        var ex = await Assert.ThrowsAsync<RenameFailedException>(
            () => CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", CancellationToken.None));

        ex.Message.Should().Be("Rename failed.");
    }

    [Fact]
    public async Task An_accepted_rename_completes_normally()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("null");

        await CreateSut(server).SendRenameRequestAsync(FileUri, 3, 4, "a new step", CancellationToken.None);

        server.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task A_target_lookup_that_times_out_throws_instead_of_reporting_no_binding()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        await Assert.ThrowsAsync<LspRequestFailedException>(
            () => CreateSut(server).GetRenameTargetsAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task A_target_lookup_rejected_with_ContentModified_throws_LspContentModifiedException()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");

        await Assert.ThrowsAsync<LspContentModifiedException>(
            () => CreateSut(server).GetRenameTargetsAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task A_target_lookup_answered_with_JSON_null_is_still_no_binding()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("null");

        (await CreateSut(server).GetRenameTargetsAsync(FileUri, 3, 4, CancellationToken.None)).Should().BeNull();
    }
}
