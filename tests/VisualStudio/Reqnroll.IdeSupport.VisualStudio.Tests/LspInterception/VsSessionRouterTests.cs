using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Issue #395's routing rule, tested directly. The point of extracting
/// <see cref="VsSessionRouter"/> from <c>LspInterceptingPipe</c> (issue #587, step 2) is that this
/// no longer needs a live pipe, two sessions and a race to exercise — the incident's failure shape
/// is now a function call.
/// </summary>
public class VsSessionRouterTests
{
    private static JObject Response(object id) => JObject.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{Json(id)},\"result\":null}}");
    private static JObject Request(object id, string method) => JObject.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{Json(id)},\"method\":\"{method}\"}}");
    private static string Json(object id) => id is string s ? $"\"{s}\"" : id.ToString()!;

    [Fact]
    public void A_response_for_the_current_session_is_delivered()
    {
        var router = new VsSessionRouter();
        router.RecordOutboundRequest("1", sessionId: 1);

        router.Route(Response(1), currentSessionId: 1, out var owner)
              .Should().Be(ResponseRouting.DeliverToCurrentSession);
        owner.Should().Be(1);
    }

    [Fact]
    public void A_response_for_an_abandoned_session_is_dropped()
    {
        // The captured #395 repro: id=143's shutdown response from session #1 arriving 71ms after
        // the swap to session #2. Delivering it hands VS's JsonRpc a response to a request it never
        // sent, which it treats as a fatal protocol violation and closes the new connection over.
        var router = new VsSessionRouter();
        router.RecordOutboundRequest("143", sessionId: 1);

        router.Route(Response(143), currentSessionId: 2, out var owner)
              .Should().Be(ResponseRouting.DropAbandoned);
        owner.Should().Be(1);
    }

    [Fact]
    public void A_server_request_or_notification_is_not_routed_as_a_response()
    {
        // Server → VS requests carry a method and must keep flowing to whichever session is current;
        // that "whichever pipe is current" policy is correct for them and only wrong for responses.
        var router = new VsSessionRouter();

        router.Route(Request(5, "window/showMessageRequest"), currentSessionId: 3, out _)
              .Should().Be(ResponseRouting.NotAResponse);
        router.Route(JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\"}"), currentSessionId: 3, out _)
              .Should().Be(ResponseRouting.NotAResponse);
    }

    [Fact]
    public void A_tracked_entry_is_consumed_by_the_response_it_matches()
    {
        // A response arrives at most once, so the entry must not linger and mis-route a later
        // message that happens to reuse the id — VS's ids are per-session integers starting at 1.
        var router = new VsSessionRouter();
        router.RecordOutboundRequest("7", sessionId: 1);

        router.Route(Response(7), currentSessionId: 1, out _).Should().Be(ResponseRouting.DeliverToCurrentSession);

        router.Route(Response(7), currentSessionId: 1, out var owner)
              .Should().Be(ResponseRouting.DropAbandoned, "the entry is gone, so nothing proves VS is waiting on it");
        owner.Should().Be(0);
    }

    [Fact]
    public void An_untracked_response_id_is_dropped_rather_than_forwarded()
    {
        // The policy inversion (design §3.5). Every VS request passes through the send pump, which
        // records it before forwarding — so an untracked response id is either a straggler whose
        // session is gone or an id VS never sent. Both are unmatched responses as far as VS's
        // JsonRpc is concerned, and it closes the connection over one of those.
        var router = new VsSessionRouter();

        router.Route(Response(99), currentSessionId: 1, out var owner)
              .Should().Be(ResponseRouting.DropAbandoned);
        owner.Should().Be(0, "nothing is tracked for this id");
    }

    [Fact]
    public void Purging_keeps_entries_from_the_most_recent_two_generations()
    {
        var router = new VsSessionRouter();
        router.RecordOutboundRequest("old", sessionId: 1);
        router.RecordOutboundRequest("recent", sessionId: 2);

        router.PurgeOlderThan(minimumLiveSessionId: 2);

        router.Route(Response("recent"), currentSessionId: 3, out var recentOwner)
              .Should().Be(ResponseRouting.DropAbandoned, "session #2's entry survives the purge and names an abandoned session");
        recentOwner.Should().Be(2);

        router.Route(Response("old"), currentSessionId: 3, out var oldOwner)
              .Should().Be(ResponseRouting.DropAbandoned,
                  "session #1's entry was purged — before the policy inversion this fell through and was " +
                  "forwarded to session #3, the latent #395 recurrence");
        oldOwner.Should().Be(0, "the purged entry can no longer name its session, which is why the default has to be to drop");
    }
}
