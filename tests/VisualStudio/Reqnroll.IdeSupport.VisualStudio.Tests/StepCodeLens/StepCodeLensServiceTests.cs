using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.StepCodeLens;

/// <summary>
/// Issue #1017: a failed or timed-out <c>textDocument/codeLens</c> request must not be cached as
/// "no lenses". Drives the real <see cref="StepCodeLensService"/> and its result cache over a real
/// <c>LspInterceptingPipe</c> talking to a scripted in-memory server.
/// </summary>
public class StepCodeLensServiceTests
{
    private const string FileUri = "file:///C:/w/Steps.cs";
    private const int ContentModified = -32801;

    private const string OneLens =
        "[{\"range\":{\"start\":{\"line\":4,\"character\":0},\"end\":{\"line\":4,\"character\":0}}," +
        "\"command\":{\"title\":\"2 step usages\",\"command\":\"reqnroll.findStepUsages\",\"arguments\":[\"" + FileUri + "\",3,8]}}]";

    private static StepCodeLensService CreateSut(ScriptedServerPipe server) =>
        new(server.Pipe, NullLogger<StepCodeLensService>.Instance);

    [Fact]
    public async Task A_ContentModified_failure_is_not_cached_and_the_next_call_refetches()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");
        server.ReplyWithResult(OneLens);
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<LspContentModifiedException>(() => sut.GetLensesAsync(FileUri, CancellationToken.None));

        var lenses = await sut.GetLensesAsync(FileUri, CancellationToken.None);

        lenses.Should().ContainSingle().Which.Title.Should().Be("2 step usages");
        server.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task A_timed_out_request_is_not_cached_and_the_next_call_refetches()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ownedRequestTimeout: TimeSpan.FromMilliseconds(200));
        server.NeverReply();
        server.ReplyWithResult(OneLens);
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetLensesAsync(FileUri, CancellationToken.None));

        var lenses = await sut.GetLensesAsync(FileUri, CancellationToken.None);

        lenses.Should().ContainSingle();
        server.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task Any_other_server_error_is_not_cached()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32603, "handler blew up");
        server.ReplyWithResult(OneLens);
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetLensesAsync(FileUri, CancellationToken.None));

        (await sut.GetLensesAsync(FileUri, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_genuine_empty_result_is_still_cached()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("[]");
        server.ReplyWithResult(OneLens); // must never be requested
        var sut = CreateSut(server);

        (await sut.GetLensesAsync(FileUri, CancellationToken.None)).Should().BeEmpty();
        (await sut.GetLensesAsync(FileUri, CancellationToken.None)).Should().BeEmpty();

        server.RequestCount.Should().Be(1, "a server that answered 'no lenses' is a real answer, cached until invalidated");
    }
}
