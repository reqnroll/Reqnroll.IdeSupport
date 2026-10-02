using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio;
using Xunit;

namespace Reqnroll.VisualStudio.Tests;

/// <summary>
/// <see cref="SupersedingCancellation"/>: the per-view "newer press cancels the older one" coordination
/// behind <see cref="GoToDefinitionCommandFilter"/> (issue #757), tested here because the filter's
/// <c>Exec</c> itself needs VS's UI thread.
/// </summary>
public class SupersedingCancellationTests
{
    [Fact]
    public void Beginning_a_new_operation_cancels_the_one_in_flight()
    {
        var sut = new SupersedingCancellation(CancellationToken.None);

        var first  = sut.Begin();
        var second = sut.Begin();

        first.IsCancellationRequested.Should().BeTrue();
        second.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void An_operation_that_ended_is_not_cancelled_by_the_next_one()
    {
        var sut = new SupersedingCancellation(CancellationToken.None);

        var first = sut.Begin();
        var firstToken = first.Token;
        sut.End(first);
        sut.Begin();

        firstToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void Ending_a_superseded_operation_does_not_forget_the_current_one()
    {
        var sut = new SupersedingCancellation(CancellationToken.None);

        var first  = sut.Begin();
        var second = sut.Begin();
        sut.End(first);   // the superseded operation finishes after the newer one started
        sut.Begin();

        second.IsCancellationRequested.Should().BeTrue("the newer operation must still be superseded by a third");
    }

    [Fact]
    public void Beginning_does_not_throw_when_the_previous_operation_was_disposed_concurrently()
    {
        // The race: the previous operation completes on a background thread and its End() disposes
        // the source after Begin() has taken it but before Begin() cancels it. Begin runs on VS's UI
        // thread, so an ObjectDisposedException there would surface from the command handler.
        var sut = new SupersedingCancellation(CancellationToken.None);
        var first = sut.Begin();
        first.Dispose();   // disposed, but still recorded as the current operation

        var begin = () => sut.Begin();

        begin.Should().NotThrow();
    }

    [Fact]
    public void The_lifetime_token_cancels_the_operation_in_flight()
    {
        using var lifetime = new CancellationTokenSource();
        var sut = new SupersedingCancellation(lifetime.Token);

        var operation = sut.Begin();
        lifetime.Cancel();

        operation.IsCancellationRequested.Should().BeTrue();
    }
}
