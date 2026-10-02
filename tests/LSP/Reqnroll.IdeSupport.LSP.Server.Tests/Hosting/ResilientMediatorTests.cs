using System.Diagnostics;
using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

/// <summary>
/// Covers <see cref="ResilientMediator"/>'s fan-out fault isolation (issue #575). Stock MediatR
/// awaits each <see cref="INotificationHandler{TNotification}"/> in a bare <c>foreach</c>, so the
/// first handler to throw suppresses every handler after it — and which ones are lost depends on
/// the order the container returns them, i.e. on assembly-scan order.
/// </summary>
/// <remarks>
/// These tests drive the mediator through a hand-built <see cref="ServiceFactory"/> rather than a
/// real DI container, so the handler <em>order</em> is fixed by the test rather than by scan order.
/// That is the point: the production defect is order-dependent, so a test that inherited the same
/// nondeterminism could pass while the bug was still live.
/// </remarks>
public class ResilientMediatorTests
{
    private sealed record TestNotification(string Value) : INotification;

    /// <summary>Records that it ran; optionally throws first.</summary>
    private sealed class RecordingHandler : INotificationHandler<TestNotification>
    {
        private readonly List<string> _log;
        private readonly string _name;
        private readonly Exception? _throw;

        public RecordingHandler(List<string> log, string name, Exception? toThrow = null)
        {
            _log = log;
            _name = name;
            _throw = toThrow;
        }

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _log.Add(_name);
            return _throw is not null ? Task.FromException(_throw) : Task.CompletedTask;
        }
    }

    private static ResilientMediator CreateSut(
        IIdeSupportLogger logger, params INotificationHandler<TestNotification>[] handlers)
    {
        ServiceFactory factory = serviceType =>
            serviceType == typeof(IEnumerable<INotificationHandler<TestNotification>>)
                ? handlers
                : throw new InvalidOperationException($"Unexpected service request: {serviceType}");

        return new ResilientMediator(factory, logger);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_prevent_later_handlers_from_running()
    {
        var log = new List<string>();
        var logger = Substitute.For<IIdeSupportLogger>();

        var sut = CreateSut(
            logger,
            new RecordingHandler(log, "first"),
            new RecordingHandler(log, "boom", new InvalidOperationException("handler failed")),
            new RecordingHandler(log, "third"));

        await sut.Publish(new TestNotification("x"), CancellationToken.None);

        // A fault in one handler must not suppress the handlers ordered after it.
        // NB: Equal(params string[]) would swallow a trailing "because" string as another
        // expected element, so the reason stays a comment here.
        log.Should().Equal("first", "boom", "third");
    }

    [Fact]
    public async Task A_handler_throwing_first_still_lets_every_later_handler_run()
    {
        // The production shape of issue #575: DiagnosticsPublishHandler throwing ahead of
        // SemanticTokensPushHandler cost the user both diagnostics AND colouring for that edit.
        var log = new List<string>();
        var logger = Substitute.For<IIdeSupportLogger>();

        var sut = CreateSut(
            logger,
            new RecordingHandler(log, "diagnostics", new InvalidOperationException("no registry")),
            new RecordingHandler(log, "semanticTokens"),
            new RecordingHandler(log, "codeLensRefresh"));

        await sut.Publish(new TestNotification("x"), CancellationToken.None);

        log.Should().Equal("diagnostics", "semanticTokens", "codeLensRefresh");
    }

    [Fact]
    public async Task Publish_does_not_rethrow_when_a_handler_throws()
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var sut = CreateSut(
            logger,
            new RecordingHandler(new List<string>(), "boom", new InvalidOperationException("x")));

        // Publish is reached from detached, unawaited paths (FireAndForget, ParseCoordinator);
        // faulting the returned Task there would surface as an unrelated warning elsewhere.
        var publish = async () => await sut.Publish(new TestNotification("x"), CancellationToken.None);

        await publish.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_handler_fault_is_logged_as_an_error_naming_the_notification()
    {
        LogMessage? logged = null;
        var logger = Substitute.For<IIdeSupportLogger>();
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(ci => logged = ci.Arg<LogMessage>());

        var sut = CreateSut(
            logger,
            new RecordingHandler(new List<string>(), "boom", new InvalidOperationException("kaboom")));

        await sut.Publish(new TestNotification("x"), CancellationToken.None);

        logged.Should().NotBeNull("a swallowed fault must still be reported");
        logged!.Level.Should().Be(TraceLevel.Error);
        logged.Message.Should().Contain(nameof(TestNotification))
            .And.Contain("kaboom",
                "the exception detail is what identifies the failing handler — the delegate " +
                "closure does not expose its own type name");
    }

    [Fact]
    public async Task A_cancelled_handler_is_not_logged_as_an_error()
    {
        LogMessage? logged = null;
        var logger = Substitute.For<IIdeSupportLogger>();
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(ci => logged = ci.Arg<LogMessage>());

        var log = new List<string>();
        var sut = CreateSut(
            logger,
            new RecordingHandler(log, "cancelled", new OperationCanceledException()),
            new RecordingHandler(log, "after"));

        await sut.Publish(new TestNotification("x"), CancellationToken.None);

        log.Should().Equal("cancelled", "after");
        // A cancelled publish is a normal outcome (a superseding edit, a closing document),
        // not a fault worth an error line.
        (logged?.Level).Should().NotBe(TraceLevel.Error);
    }

    [Fact]
    public async Task All_handlers_run_when_none_throw()
    {
        var log = new List<string>();
        var logger = Substitute.For<IIdeSupportLogger>();

        var sut = CreateSut(
            logger,
            new RecordingHandler(log, "a"),
            new RecordingHandler(log, "b"),
            new RecordingHandler(log, "c"));

        await sut.Publish(new TestNotification("x"), CancellationToken.None);

        log.Should().Equal("a", "b", "c");
        logger.DidNotReceive().Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Error));
    }
}
