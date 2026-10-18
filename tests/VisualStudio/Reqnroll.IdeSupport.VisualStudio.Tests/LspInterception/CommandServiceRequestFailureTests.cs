using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.Utilities;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

/// <summary>
/// Issue #1020: Find Unused Step Definitions, Find Step Usages, Go To Step Definition and Go To Hooks
/// must report a failed or timed-out request as a failure — never as "0 unused", "not a binding",
/// "no step definition found" or "no hooks". Each service is driven over a real
/// <c>LspInterceptingPipe</c> talking to a scripted in-memory server.
/// </summary>
public class CommandServiceRequestFailureTests
{
    private const string FileUri = "file:///C:/w/Sample.feature";
    private const int ContentModified = -32801;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    // ── Find Unused Step Definitions ──────────────────────────────────────────────

    private static FindUnusedStepDefinitionsService Unused(ScriptedServerPipe s) =>
        new(s.Pipe, NullLogger<FindUnusedStepDefinitionsService>.Instance);

    [Fact]
    public async Task FindUnused_timeout_throws_instead_of_reporting_none_unused()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        await Assert.ThrowsAsync<LspRequestFailedException>(() => Unused(server).FindUnusedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindUnused_server_error_throws()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32603, "handler blew up");

        var ex = await Assert.ThrowsAsync<LspRequestFailedException>(() => Unused(server).FindUnusedAsync(CancellationToken.None));
        ex.Reason.Should().Contain("handler blew up");
    }

    [Fact]
    public async Task FindUnused_a_genuine_empty_answer_is_still_empty()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("{\"items\":[]}");

        (await Unused(server).FindUnusedAsync(CancellationToken.None)).Items.Should().BeEmpty();
    }

    // ── Find Step Usages ──────────────────────────────────────────────────────────

    private static FindStepUsagesService Usages(ScriptedServerPipe s) =>
        new(s.Pipe, NullLogger<FindStepUsagesService>.Instance);

    [Fact]
    public async Task FindUsages_timeout_throws_instead_of_reporting_not_a_binding()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        await Assert.ThrowsAsync<LspRequestFailedException>(() => Usages(server).FindUsagesAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task FindUsages_ContentModified_throws_LspContentModifiedException()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");

        await Assert.ThrowsAsync<LspContentModifiedException>(() => Usages(server).FindUsagesAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task FindUsages_an_answer_of_JSON_null_is_still_NotABinding()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("null");

        (await Usages(server).FindUsagesAsync(FileUri, 3, 4, CancellationToken.None)).IsBinding.Should().BeFalse();
    }

    [Fact]
    public async Task FindUsages_a_binding_with_no_usages_is_still_a_binding()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("{\"isBinding\":true,\"locations\":[]}");

        var result = await Usages(server).FindUsagesAsync(FileUri, 3, 4, CancellationToken.None);

        result.IsBinding.Should().BeTrue();
        result.Locations.Should().BeEmpty();
    }

    // ── Go To Step Definition ─────────────────────────────────────────────────────

    private static FindStepDefinitionsService Definitions(ScriptedServerPipe s) =>
        new(s.Pipe, NullLogger<FindStepDefinitionsService>.Instance);

    [Fact]
    public async Task GetDefinitions_timeout_throws_instead_of_reporting_none_found()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        await Assert.ThrowsAsync<LspRequestFailedException>(() => Definitions(server).GetDefinitionsAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task GetDefinitions_a_genuine_empty_answer_is_still_empty()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("{\"items\":[]}");

        (await Definitions(server).GetDefinitionsAsync(FileUri, 3, 4, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Ctrl_hover_probe_treats_a_failed_request_as_not_navigable_rather_than_throwing()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(ContentModified, "content modified");

        (await Definitions(server).HasNavigableDefinitionAsync(FileUri, 3, 4, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_Ctrl_hover_probe_treats_a_timeout_as_not_navigable()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        (await Definitions(server).HasNavigableDefinitionAsync(FileUri, 3, 4, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_lookup_is_an_OperationCanceledException_not_an_empty_answer()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.NeverReply();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Definitions(server).GetDefinitionsAsync(FileUri, 3, 4, cts.Token));
    }

    // ── Go To Hooks ───────────────────────────────────────────────────────────────

    private static FindHooksService Hooks(ScriptedServerPipe s) =>
        new(s.Pipe, NullLogger<FindHooksService>.Instance);

    [Fact]
    public async Task FindHooks_timeout_throws_instead_of_reporting_no_hooks()
    {
        await using var server = await ScriptedServerPipe.StartAsync(ShortTimeout);
        server.NeverReply();

        await Assert.ThrowsAsync<LspRequestFailedException>(() => Hooks(server).FindHooksAsync(FileUri, 3, 4, CancellationToken.None));
    }

    [Fact]
    public async Task FindHooks_server_error_throws()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithError(-32603, "handler blew up");

        await Assert.ThrowsAsync<LspRequestFailedException>(() => Hooks(server).FindHooksAsync(FileUri, 3, 4, ownLevelOnly: true, CancellationToken.None));
    }

    [Fact]
    public async Task FindHooks_a_genuine_empty_answer_is_still_no_hooks()
    {
        await using var server = await ScriptedServerPipe.StartAsync();
        server.ReplyWithResult("{\"hooks\":[]}");

        (await Hooks(server).FindHooksAsync(FileUri, 3, 4, CancellationToken.None)).Hooks.Should().BeEmpty();
    }
}
