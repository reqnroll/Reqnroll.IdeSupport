using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.TestOutcomes;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// Phase 1 exit criterion for the MTP test-outcome plan (issue #715): a real
/// <see cref="TestOutcomeTcpListener"/> bind/dispose cycle, wired to a real
/// <see cref="TestOutcomeSessionBreadcrumb"/>, produces a correctly-shaped breadcrumb file on bind and
/// removes it on clean shutdown. <see cref="TestOutcomeSessionBreadcrumbTests"/> (LSP.Core.Tests)
/// covers the breadcrumb component's own write/prune logic in isolation.
/// </summary>
public class TestOutcomeTcpListenerBreadcrumbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-listener-breadcrumb-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const int Pid = 424242;

    private (TestOutcomeTcpListener Listener, string ExpectedFile) CreateListener(string? workspaceRoot = @"C:\repo\MySolution")
    {
        var breadcrumb = new TestOutcomeSessionBreadcrumb(_dir, Pid, _ => true, _logger);
        var listener = new TestOutcomeTcpListener(new TestOutcomeStore(), _logger, () => { },
            persistence: null, breadcrumb: breadcrumb, resolveWorkspaceRoot: () => workspaceRoot);
        return (listener, Path.Combine(_dir, $"{Pid}.json"));
    }

    [Fact]
    public void RegisterRun_binding_the_listener_writes_a_correctly_shaped_breadcrumb_file()
    {
        var (listener, file) = CreateListener();
        using var _ = listener;

        var registration = listener.RegisterRun();

        registration.Should().NotBeNull();
        File.Exists(file).Should().BeTrue();
        var json = JObject.Parse(File.ReadAllText(file));
        json.Value<string>("endpoint").Should().Be(listener.Endpoint).And.Be(registration!.Endpoint);
        json.Value<string>("workspaceRoot").Should().Be(@"C:\repo\MySolution");
        json.Value<int>("lspServerPid").Should().Be(Pid);
        json.Value<DateTime?>("startedUtc").Should().NotBeNull();
    }

    [Fact]
    public void Dispose_after_binding_deletes_the_breadcrumb_file()
    {
        var (listener, file) = CreateListener();
        listener.RegisterRun();
        File.Exists(file).Should().BeTrue("guard: the breadcrumb must exist before testing its removal");

        listener.Dispose();

        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public void Dispose_without_ever_binding_does_not_throw()
    {
        var (listener, _) = CreateListener();

        Action act = () => listener.Dispose();

        act.Should().NotThrow();
    }
}
