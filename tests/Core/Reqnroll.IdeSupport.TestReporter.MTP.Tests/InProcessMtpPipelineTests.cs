using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;
using Reqnroll.IdeSupport.TestReporter.MTP.Tests.Mtp;

namespace Reqnroll.IdeSupport.TestReporter.MTP.Tests;

/// <summary>
/// Issue #741 T0: the reporter driven through Microsoft.Testing.Platform's real in-process pipeline
/// (<c>TestApplication.CreateBuilderAsync</c> + a <see cref="FakeTestFramework"/>) rather than by calling
/// its methods directly — covers extension enablement (<c>IsEnabledAsync</c>), the session lifetime
/// hooks and message-bus delivery in seconds, with no <c>dotnet test</c> process or fixture project.
/// </summary>
public class InProcessMtpPipelineTests
{
    private static TestNode Node(string method, string displayName, TestNodeStateProperty state, params string[] parameterTypes)
    {
        var properties = new PropertyBag();
        properties.Add(state);
        properties.Add(new TestMethodIdentifierProperty("Fixture, Version=1.0.0.0", "Sample.Features", "CalculatorFeature", method, 0, parameterTypes, "System.Void"));
        return new TestNode { Uid = new TestNodeUid(Guid.NewGuid().ToString()), DisplayName = displayName, Properties = properties };
    }

    /// <summary>Runs an in-process MTP application whose only reporter is ours, wired exactly as TestingPlatformBuilderHook wires it but with the breadcrumb lookup replaced by <paramref name="findEndpoint"/>.</summary>
    private static async Task<int> RunAsync(IReadOnlyList<TestNode> nodes, Func<string?> findEndpoint)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), "reqnroll-mtp-inproc", Guid.NewGuid().ToString("N"));
        var builder = await TestApplication.CreateBuilderAsync(["--results-directory", resultsDirectory]);
        builder.RegisterTestFramework(_ => new TestFrameworkCapabilities(), (_, _) => new FakeTestFramework(nodes));

        var factory = new CompositeExtensionFactory<ReqnrollMtpReporter>(_ => new ReqnrollMtpReporter(findEndpoint));
        builder.TestHost.AddTestSessionLifetimeHandler(factory); // This test project runs on MTP 2.2.3.
        builder.TestHost.AddDataConsumer(factory);

        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }

    private static (TcpListener Listener, string Endpoint, Task<List<string>> Lines) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var lines = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
            var received = new List<string>();
            while (await reader.ReadLineAsync() is { } line) received.Add(line);
            return received;
        });
        return (listener, endpoint, lines);
    }

    [Fact]
    public async Task Streams_hello_one_result_per_terminal_update_and_runComplete_through_the_real_pipeline()
    {
        var (listener, endpoint, linesTask) = Listen();
        using var _ = listener;
        var nodes = new[]
        {
            Node("AddingTwoNumbers", "Adding two numbers", DiscoveredTestNodeStateProperty.CachedInstance),
            Node("AddingTwoNumbers", "Adding two numbers", InProgressTestNodeStateProperty.CachedInstance),
            Node("AddingTwoNumbers", "Adding two numbers", PassedTestNodeStateProperty.CachedInstance),
            Node("AStepFails", "A step fails", new FailedTestNodeStateProperty(new InvalidOperationException("boom"), "boom")),
            Node("NotRun", "Not run", SkippedTestNodeStateProperty.CachedInstance),
            Node("TooSlow", "Too slow", new TimeoutTestNodeStateProperty("timed out")),
            Node("Crashed", "Crashed", new ErrorTestNodeStateProperty(new InvalidOperationException("crash"), "crash")),
#pragma warning disable CS0618 // Obsolete, but some frameworks still emit it: the reporter must map it (issue #741 §2.2).
            Node("Cancelled", "Cancelled", new CancelledTestNodeStateProperty("cancelled")),
#pragma warning restore CS0618
            Node("AddingRows", "Adding rows(1,2,3)", PassedTestNodeStateProperty.CachedInstance, "System.String", "System.String[]"),
            Node("AddingRows", "Adding rows(5,5,11)", new FailedTestNodeStateProperty("wrong sum"), "System.String", "System.String[]"),
        };

        await RunAsync(nodes, () => endpoint);

        var messages = (await linesTask.WaitAsync(TimeSpan.FromSeconds(30))).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        messages.First().GetProperty("type").GetString().Should().Be("hello");
        messages.Last().GetProperty("type").GetString().Should().Be("runComplete");

        var results = messages.Where(m => m.GetProperty("type").GetString() == "result").ToList();
        results.Should().HaveCount(8, "discovered/in-progress updates are not results (#718 regression)");
        string OutcomeOf(string display) => results.Single(r => r.GetProperty("displayName").GetString() == display).GetProperty("outcome").GetString()!;
        OutcomeOf("Adding two numbers").Should().Be("Passed");
        OutcomeOf("A step fails").Should().Be("Failed");
        OutcomeOf("Not run").Should().Be("Skipped");
        OutcomeOf("Too slow").Should().Be("Failed");
        OutcomeOf("Crashed").Should().Be("Failed");
        OutcomeOf("Cancelled").Should().Be("Skipped");

        var rows = results.Where(r => r.GetProperty("managedMethod").GetString()!.StartsWith("AddingRows(")).ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.GetProperty("managedMethod").GetString()).Distinct().Should().ContainSingle()
            .Which.Should().Be("AddingRows(System.String,System.String[])", "outline rows share one row-invariant identity");

        messages.Last().GetProperty("executed").GetInt32().Should().Be(8);
    }

    [Fact]
    public async Task With_no_matching_session_the_extension_is_disabled_and_never_connects()
    {
        var (listener, _, linesTask) = Listen();
        using var __ = listener;
        var lookups = 0;

        await RunAsync([Node("AddingTwoNumbers", "Adding two numbers", PassedTestNodeStateProperty.CachedInstance)], () => { lookups++; return null; });

        lookups.Should().Be(1, "the breadcrumb lookup happens once, in IsEnabledAsync, and is cached for the session");
        var finished = await Task.WhenAny(linesTask, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.Should().NotBeSameAs(linesTask, "a disabled reporter must not open a connection");
    }

    [Fact]
    public async Task The_session_connects_to_the_endpoint_IsEnabledAsync_resolved_and_looks_it_up_once()
    {
        var (listener, endpoint, linesTask) = Listen();
        using var _ = listener;
        var lookups = 0;

        await RunAsync([Node("AddingTwoNumbers", "Adding two numbers", PassedTestNodeStateProperty.CachedInstance)], () => { lookups++; return endpoint; });

        lookups.Should().Be(1);
        (await linesTask.WaitAsync(TimeSpan.FromSeconds(30))).Should().HaveCount(3, "hello, one result, runComplete");
    }
}
