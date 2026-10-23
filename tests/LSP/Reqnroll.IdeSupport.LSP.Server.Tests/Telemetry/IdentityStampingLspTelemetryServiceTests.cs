#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class IdentityStampingLspTelemetryServiceTests
{
    private readonly ILspTelemetryService _inner = Substitute.For<ILspTelemetryService>();

    private Dictionary<string, object?> Send(
        ClientIdeContext ide, Dictionary<string, object?>? properties = null)
    {
        Dictionary<string, object?>? captured = null;
        _inner
            .When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(ci => captured = ci.Arg<Dictionary<string, object?>>());

        new IdentityStampingLspTelemetryService(_inner, ide, "9.9.9", "session-1")
            .SendEvent("Evt", properties ?? new Dictionary<string, object?>());

        return captured!;
    }

    [Fact]
    public void Stamps_ide_client_server_version_and_session_id()
    {
        var ide = new ClientIdeContext("rider");
        ide.ApplyClientInfo(new ClientInfo { Name = "Rider", Version = "2025.1" });

        var sent = Send(ide);

        sent["IdeClient"].Should().Be("rider");
        // The IDE version is host-stamped (IdeVersion); the self-reported ClientInfo.Version is
        // log-only, so there is deliberately a single source for it.
        sent.Should().NotContainKey("IdeClientVersion");
        sent["ServerVersion"].Should().Be("9.9.9");
        sent["SessionId"].Should().Be("session-1");
    }

    [Fact]
    public void Stamps_the_client_session_id_when_the_client_supplied_one()
    {
        var id = "3f2b8c1e-0a4d-4e6f-9b7a-1c2d3e4f5a6b";

        var sent = Send(new ClientIdeContext("vscode", clientSessionId: id.ToUpperInvariant()));

        sent["ClientSessionId"].Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid C:/Users/someone")]
    public void Omits_the_client_session_id_unless_it_is_a_guid(string? raw)
    {
        var sent = Send(new ClientIdeContext("vscode", clientSessionId: raw));

        sent.Should().NotContainKey("ClientSessionId");
    }

    [Fact]
    public void Omits_identity_values_that_are_not_known_yet()
    {
        var sent = Send(new ClientIdeContext(null));

        sent.Should().NotContainKey("IdeClient");
        sent.Should().ContainKey("ServerVersion");
        sent.Should().ContainKey("SessionId");
    }

    [Fact]
    public void Does_not_clobber_caller_supplied_keys_or_mutate_the_callers_dictionary()
    {
        var original = new Dictionary<string, object?> { ["IdeClient"] = "explicit", ["Other"] = 1 };

        var sent = Send(new ClientIdeContext("vscode"), original);

        sent["IdeClient"].Should().Be("explicit");
        sent["Other"].Should().Be(1);
        sent["SessionId"].Should().Be("session-1");
        original.Keys.Should().BeEquivalentTo("IdeClient", "Other");
    }

    [Fact]
    public void Public_constructor_generates_a_distinct_session_id_per_instance()
    {
        string? Id()
        {
            string? id = null;
            _inner
                .When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
                .Do(ci => id = (string?)ci.Arg<Dictionary<string, object?>>()["SessionId"]);
            new IdentityStampingLspTelemetryService(_inner, new ClientIdeContext("vscode"))
                .SendEvent("Evt", new Dictionary<string, object?>());
            return id;
        }

        Id().Should().NotBeNullOrEmpty().And.NotBe(Id());
    }

    [Fact]
    public void Public_constructor_stamps_the_session_id_the_client_chose()
    {
        Dictionary<string, object?>? sent = null;
        _inner.When(t => t.SendEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()))
            .Do(ci => sent = ci.Arg<Dictionary<string, object?>>());

        new IdentityStampingLspTelemetryService(_inner, new ClientIdeContext("vscode"), "6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00")
            .SendEvent("Evt", new Dictionary<string, object?>());

        sent!["SessionId"].Should().Be("6f1c2b8e-0d3a-4c55-9b7e-2a4d8c1e9f00");
    }
}
