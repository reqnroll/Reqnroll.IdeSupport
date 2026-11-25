using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

public class ReqnrollProjectPresenceNotifierTests
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IReqnrollProjectDetector _detector = Substitute.For<IReqnrollProjectDetector>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private int _sent;

    private ReqnrollProjectPresenceNotifier CreateSut() =>
        new(_scopeManager, _detector, () => _sent++, _logger);

    private IProjectScope Project(bool usesReqnroll)
    {
        var project = Substitute.For<IProjectScope>();
        project.ProjectName.Returns(usesReqnroll ? "Specs" : "Library");
        _detector.IsReqnrollTestProject(project).Returns(usesReqnroll);
        return project;
    }

    [Fact]
    public void Sends_nothing_when_no_project_uses_Reqnroll()
    {
        var sut = CreateSut();
        sut.OnServerStarted();

        sut.Evaluate(Project(usesReqnroll: false));

        sut.IsDetected.Should().BeFalse();
        _sent.Should().Be(0);
    }

    [Fact]
    public void Sends_once_when_the_first_Reqnroll_project_is_seen()
    {
        var sut = CreateSut();
        sut.OnServerStarted();

        sut.Evaluate(Project(usesReqnroll: false));
        sut.Evaluate(Project(usesReqnroll: true));
        sut.Evaluate(Project(usesReqnroll: true));

        sut.IsDetected.Should().BeTrue();
        _sent.Should().Be(1);
    }

    [Fact]
    public void Waits_for_the_handshake_when_the_project_arrives_first()
    {
        var sut = CreateSut();

        sut.Evaluate(Project(usesReqnroll: true));
        _sent.Should().Be(0, "no notification may be sent before the server has started");

        sut.OnServerStarted();
        sut.OnServerStarted();
        _sent.Should().Be(1);
    }

    [Fact]
    public void Checks_each_project_as_the_scope_manager_reports_it()
    {
        var sut = CreateSut();
        sut.OnServerStarted();
        using var project = Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.DiscoveryTestSupport.MakeProject(
            new LspIdeScope(_logger), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        _detector.IsReqnrollTestProject(project).Returns(true);

        _scopeManager.ProjectDiscovered += Raise.Event<Action<LspReqnrollProject>>(project);

        _sent.Should().Be(1);
    }

    [Fact]
    public void A_failing_check_does_not_throw_and_a_later_check_can_still_detect()
    {
        var sut = CreateSut();
        sut.OnServerStarted();
        var project = Substitute.For<IProjectScope>();
        _detector.IsReqnrollTestProject(project).Returns(_ => throw new IOException("locked"), _ => true);

        sut.Invoking(s => s.Evaluate(project)).Should().NotThrow();
        _sent.Should().Be(0);

        sut.Evaluate(project);
        _sent.Should().Be(1);
    }

    [Fact]
    public void A_failing_send_is_logged_and_not_thrown()
    {
        var sut = new ReqnrollProjectPresenceNotifier(
            _scopeManager, _detector, () => throw new InvalidOperationException("closed"), _logger);
        sut.OnServerStarted();

        sut.Invoking(s => s.Evaluate(Project(usesReqnroll: true))).Should().NotThrow();
        _logger.Received().Log(Arg.Is<LogMessage>(m => m.Message.Contains("closed")));
    }
}
