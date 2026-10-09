using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Extension.TestTargets;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.TestTargets;

/// <summary>
/// Issue #1017: a failed or timed-out resolve request must surface as an exception (so the Run
/// CodeLens cache, which retries faulted entries, never stores it) rather than as "no targets".
/// </summary>
public class ScenarioTestTargetServiceTests
{
    private const string FileUri = "file:///C:/w/Sample.feature";
    private const int ContentModified = -32801;
    private static readonly GherkinSymbolRange Range = new(new GherkinSymbolPosition(2, 0), new GherkinSymbolPosition(5, 0));

    private const string OneTarget =
        "{\"targets\":[{\"declaringTypeFullName\":\"My.Feature\",\"methodName\":\"Scenario1\",\"isParameterized\":false}]}";

    private static ScenarioTestTargetService CreateSut(ScriptedServerPipe server) =>
        new(server.Pipe, NullLogger<ScenarioTestTargetService>.Instance);

    [Fact]
    public async Task A_ContentModified_error_throws_LspContentModifiedException_instead_of_returning_no_targets()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");
        server.ReplyWithResult(OneTarget);
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<LspContentModifiedException>(
            () => sut.ResolveTestTargetsAsync(FileUri, Range, CancellationToken.None));

        var targets = await sut.ResolveTestTargetsAsync(FileUri, Range, CancellationToken.None);
        targets.Should().ContainSingle().Which.MethodName.Should().Be("Scenario1");
    }

    [Fact]
    public async Task A_timeout_throws_instead_of_returning_no_targets()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ownedRequestTimeout: TimeSpan.FromMilliseconds(200));
        server.NeverReply();
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveTestTargetsAsync(FileUri, Range, CancellationToken.None));
    }

    [Fact]
    public async Task A_container_request_failure_throws_instead_of_returning_no_targets()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");
        var sut = CreateSut(server);

        await Assert.ThrowsAsync<LspContentModifiedException>(
            () => sut.ResolveContainerTestTargetsAsync(FileUri, Range, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_throws_OperationCanceledException()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();
        var sut = CreateSut(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ResolveTestTargetsAsync(FileUri, Range, cts.Token));
    }

    [Fact]
    public async Task A_genuine_empty_answer_still_maps_to_no_targets()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("{}");
        server.ReplyWithResult("{\"targets\":[]}");
        var sut = CreateSut(server);

        (await sut.ResolveTestTargetsAsync(FileUri, Range, CancellationToken.None)).Should().BeEmpty();
        (await sut.ResolveContainerTestTargetsAsync(FileUri, Range, CancellationToken.None)).Should().BeEmpty();
    }
}
