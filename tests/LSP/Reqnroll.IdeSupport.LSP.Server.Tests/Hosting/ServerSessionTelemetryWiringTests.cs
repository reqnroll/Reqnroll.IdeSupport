using Microsoft.Extensions.DependencyInjection;
using Nerdbank.Streams;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

/// <summary>
/// Issue #845: proves <see cref="Program.ConfigureServer"/> actually wires <c>ServerSessionStarted</c> to the
/// LSP handshake, using a real in-process client over an in-memory pipe (the unit tests in
/// <c>ServerSessionTelemetryTests</c> cover the event's content).
/// </summary>
public class ServerSessionTelemetryWiringTests
{
    [Fact]
    public async Task ServerSessionStarted_is_sent_once_the_handshake_completes_with_the_client_version()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        var sent = new TaskCompletionSource<Dictionary<string, object?>>();
        telemetry.When(t => t.SendEvent(TelemetryEvents.ServerSessionStarted, Arg.Any<Dictionary<string, object?>>()))
            .Do(c => sent.TrySetResult(c.Arg<Dictionary<string, object?>>()));

        var (clientPipe, serverPipe) = FullDuplexStream.CreatePair();
        var server = LanguageServer.PreInit(options =>
        {
            options.WithInput(serverPipe).WithOutput(serverPipe);
            Program.ConfigureServer(options, "rider");
            // Last registration wins: capture what the server would send instead of transmitting it.
            options.Services.AddSingleton(telemetry);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverInit = server.Initialize(cts.Token);
        using var client = await LanguageClient.From(options => options
            .WithInput(clientPipe).WithOutput(clientPipe)
            .WithClientInfo(new ClientInfo { Name = "Rider", Version = "2025.1" })
            .WithRootUri(OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath(Path.GetTempPath())), cts.Token);
        await serverInit;

        var properties = await sent.Task.WaitAsync(TimeSpan.FromSeconds(20));
        properties["ClientVersion"].Should().Be("2025.1");
        properties["StartupMs"].Should().BeOfType<long>();
    }
}
