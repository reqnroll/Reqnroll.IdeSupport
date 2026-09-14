using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// The JSON-RPC shape classifiers and builders extracted from <c>LspInterceptingPipe</c>
/// (issue #587, step 2). Small, but the distinctions they encode are what the correlation and
/// routing rules are built on, and one of them (<c>"id":null</c>) is a real incident.
/// </summary>
public class LspJsonRpcTests
{
    [Fact]
    public void Exit_is_recognised_as_the_end_of_session_marker()
    {
        LspJsonRpc.IsExitNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}"))
                  .Should().BeTrue();
    }

    [Fact]
    public void Exit_carrying_an_explicit_json_null_id_is_still_recognised()
    {
        // JObject["id"] returns a JTokenType.Null token for "id":null, not a C# null, so a check for
        // the latter alone misses this frame — leaving the connection looking alive after its server
        // had been told to leave, which is the exact #555 failure the detection exists to prevent.
        LspJsonRpc.IsExitNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"exit\"}"))
                  .Should().BeTrue();
    }

    [Fact]
    public void Shutdown_and_an_exit_request_are_not_exit_notifications()
    {
        LspJsonRpc.IsExitNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"shutdown\"}"))
                  .Should().BeFalse("only exit ends the process; treating shutdown as terminal throws away a usable connection");
        LspJsonRpc.IsExitNotification(JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"exit\"}"))
                  .Should().BeFalse("a frame with a real id is a request, not the exit notification");
    }

    [Fact]
    public void Requests_responses_and_notifications_are_told_apart()
    {
        var request      = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
        var response     = JObject.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
        var notification = JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");

        LspJsonRpc.TryGetRequestId(request, out var requestId).Should().BeTrue();
        requestId.Should().Be("1");
        LspJsonRpc.TryGetRequestId(response, out _).Should().BeFalse();
        LspJsonRpc.TryGetRequestId(notification, out _).Should().BeFalse("a notification has no id to track");

        LspJsonRpc.TryGetResponseId(response, out var responseId).Should().BeTrue();
        responseId.Should().Be("1");
        LspJsonRpc.TryGetResponseId(request, out _).Should().BeFalse();
        LspJsonRpc.TryGetResponseId(notification, out _).Should().BeFalse();
    }

    [Fact]
    public void Built_messages_are_valid_json_rpc_with_and_without_params()
    {
        var notification = JObject.Parse(LspJsonRpc.BuildNotification("reqnroll/projectLoaded", "{\"path\":\"a.csproj\"}"));
        notification["method"]!.Value<string>().Should().Be("reqnroll/projectLoaded");
        notification["params"]!["path"]!.Value<string>().Should().Be("a.csproj");
        notification.ContainsKey("id").Should().BeFalse("a notification must not carry an id");

        var bare = JObject.Parse(LspJsonRpc.BuildNotification("reqnroll/ping", null));
        bare.ContainsKey("params").Should().BeFalse("an omitted params field must be absent, not null");

        var request = JObject.Parse(LspJsonRpc.BuildRequest("reqnroll-rpc-abc", "textDocument/codeLens", null));
        request["id"]!.Value<string>().Should().Be("reqnroll-rpc-abc");
        request["method"]!.Value<string>().Should().Be("textDocument/codeLens");
    }

    [Fact]
    public void A_method_name_needing_escaping_survives_the_round_trip()
    {
        var built = LspJsonRpc.BuildNotification("weird/\"quoted\"\\name", null);

        JObject.Parse(built)["method"]!.Value<string>().Should().Be("weird/\"quoted\"\\name");
    }
}
