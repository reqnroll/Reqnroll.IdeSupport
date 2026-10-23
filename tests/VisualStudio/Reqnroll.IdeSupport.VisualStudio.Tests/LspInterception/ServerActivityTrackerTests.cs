using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LspInterception;

public class ServerActivityTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static Task SendRequest(ServerActivityTracker sut, int id, string method) =>
        sut.InterceptAsync(
            new LspMessage(LspMessageDirection.Send, JObject.FromObject(new { jsonrpc = "2.0", id, method }), Start),
            CancellationToken.None);

    private static Task ReceiveResponse(ServerActivityTracker sut, int id) =>
        sut.InterceptAsync(
            new LspMessage(LspMessageDirection.Receive, JObject.FromObject(new { jsonrpc = "2.0", id, result = (object?)null }), Start),
            CancellationToken.None);

    [Fact]
    public async Task Requests_without_a_response_are_in_flight_newest_first()
    {
        var now = Start;
        var sut = new ServerActivityTracker(null, () => now);
        await SendRequest(sut, 1, "textDocument/completion");
        await SendRequest(sut, 2, "textDocument/hover");
        await SendRequest(sut, 3, "reqnroll/resolveTestTargets");
        await ReceiveResponse(sut, 2);
        now = Start.AddSeconds(4);

        var details = sut.Describe(now);

        details.Should().Contain(ServerActivityTracker.InFlightRequestCountKey, 2)
            .And.Contain(ServerActivityTracker.InFlightMethodsKey, "reqnroll/resolveTestTargets,textDocument/completion")
            .And.Contain(ServerActivityTracker.LastRequestMethodKey, "reqnroll/resolveTestTargets")
            .And.Contain(ServerActivityTracker.LastRequestAgeMsKey, 4000L);
    }

    [Fact]
    public async Task Only_a_few_distinct_methods_are_reported()
    {
        var sut = new ServerActivityTracker(null, () => Start);
        for (var i = 0; i < 20; i++)
            await SendRequest(sut, i, i % 2 == 0 ? "textDocument/hover" : $"custom/m{i}");

        var methods = (string)sut.Describe(Start)[ServerActivityTracker.InFlightMethodsKey];

        methods.Split(',').Should().HaveCount(ServerActivityTracker.MaxReportedMethods).And.OnlyHaveUniqueItems();
        methods.Should().StartWith("custom/m19,textDocument/hover");
    }

    [Fact]
    public async Task The_record_is_bounded_when_responses_never_arrive()
    {
        var sut = new ServerActivityTracker(null, () => Start);
        for (var i = 0; i < ServerActivityTracker.MaxTracked + 40; i++)
            await SendRequest(sut, i, "textDocument/hover");

        sut.Describe(Start)[ServerActivityTracker.InFlightRequestCountKey].Should().Be(ServerActivityTracker.MaxTracked);
    }

    [Fact]
    public async Task A_method_that_is_not_a_plain_method_name_is_reported_as_other()
    {
        var sut = new ServerActivityTracker(null, () => Start);
        await SendRequest(sut, 1, "weird method with C:\\path");

        sut.Describe(Start)[ServerActivityTracker.LastRequestMethodKey].Should().Be("other");
    }

    [Fact]
    public async Task Notifications_and_requests_from_the_server_are_not_tracked()
    {
        var sut = new ServerActivityTracker(null, () => Start);
        await sut.InterceptAsync(
            new LspMessage(LspMessageDirection.Send, JObject.FromObject(new { jsonrpc = "2.0", method = "textDocument/didChange" }), Start),
            CancellationToken.None);
        await sut.InterceptAsync(
            new LspMessage(LspMessageDirection.Receive, JObject.FromObject(new { jsonrpc = "2.0", id = 9, method = "workspace/configuration" }), Start),
            CancellationToken.None);

        var details = sut.Describe(Start);

        details.Should().Contain(ServerActivityTracker.InFlightRequestCountKey, 0)
            .And.NotContainKey(ServerActivityTracker.LastRequestMethodKey);
    }

    [Fact]
    public async Task Memory_is_sampled_from_the_process_on_traffic()
    {
        using var process = Process.GetCurrentProcess();
        var now = Start;
        var sut = new ServerActivityTracker(process, () => now);

        sut.Describe(now).Should().NotContainKey(ServerActivityTracker.PeakWorkingSetMbKey);
        await SendRequest(sut, 1, "textDocument/hover");

        var details = sut.Describe(now);
        ((long)details[ServerActivityTracker.PeakWorkingSetMbKey]).Should().BePositive();
        ((long)details[ServerActivityTracker.PrivateMemoryMbKey]).Should().BePositive();
    }

    [Fact]
    public void A_tracker_without_a_process_reports_no_memory()
    {
        new ServerActivityTracker(null).Describe(Start).Should().NotContainKey(ServerActivityTracker.PeakWorkingSetMbKey);
    }
}
