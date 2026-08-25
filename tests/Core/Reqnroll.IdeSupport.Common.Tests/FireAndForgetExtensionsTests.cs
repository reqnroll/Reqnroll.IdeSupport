using AwesomeAssertions;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests;

/// <summary>
/// Unit tests for <see cref="FireAndForgetExtensions"/> (issue #477): discarding a Task
/// reference does not defer an async method's synchronous prefix off the calling thread, and
/// silently swallows any exception it throws. These tests pin down that the
/// <see cref="Func{Task}"/> overload genuinely runs its work off the calling thread, and that
/// both overloads log (rather than lose) a fault.
/// </summary>
public class FireAndForgetExtensionsTests
{
    [Fact]
    public async Task FireAndForget_of_a_task_factory_runs_the_work_off_the_calling_thread()
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var observedThreadId = -1;
        var completed = new TaskCompletionSource<bool>();

        FireAndForgetExtensions.FireAndForget(
            () =>
            {
                observedThreadId = Environment.CurrentManagedThreadId;
                completed.SetResult(true);
                return Task.CompletedTask;
            },
            logger, "test");

        await completed.Task;
        observedThreadId.Should().NotBe(callingThreadId,
            "the work must run on a thread-pool thread, not inline on the caller");
    }

    [Fact]
    public async Task FireAndForget_of_a_faulting_task_logs_the_exception_instead_of_losing_it()
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        LogMessage? logged = null;
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(ci => logged = ci.Arg<LogMessage>());

        var tcs = new TaskCompletionSource<bool>();
        tcs.SetException(new InvalidOperationException("boom"));

        FireAndForgetExtensions.FireAndForget(tcs.Task, logger, "test-context");

        // ContinueWith with ExecuteSynchronously on an already-completed task runs its
        // continuation before the call above returns, but give it a beat regardless.
        await Task.Delay(50);

        logged.Should().NotBeNull();
        logged!.Level.Should().Be(TraceLevel.Error);
        logged.Message.Should().Contain("test-context").And.Contain("boom");
    }

    [Fact]
    public async Task FireAndForget_of_a_faulting_task_factory_logs_the_exception()
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        LogMessage? logged = null;
        logger.When(l => l.Log(Arg.Any<LogMessage>())).Do(ci => logged = ci.Arg<LogMessage>());

        FireAndForgetExtensions.FireAndForget(
            () => throw new InvalidOperationException("kaboom"),
            logger, "factory-context");

        // Poll briefly: the throw happens on a background thread-pool thread.
        for (var i = 0; i < 50 && logged is null; i++)
            await Task.Delay(20);

        logged.Should().NotBeNull();
        logged!.Message.Should().Contain("factory-context").And.Contain("kaboom");
    }

    [Fact]
    public void FireAndForget_of_a_successful_task_does_not_log_anything()
    {
        var logger = Substitute.For<IIdeSupportLogger>();

        FireAndForgetExtensions.FireAndForget(Task.CompletedTask, logger, "ok-context");

        logger.DidNotReceive().Log(Arg.Any<LogMessage>());
    }
}
