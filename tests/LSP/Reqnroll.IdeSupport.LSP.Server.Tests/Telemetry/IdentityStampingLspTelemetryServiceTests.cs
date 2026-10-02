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
}
