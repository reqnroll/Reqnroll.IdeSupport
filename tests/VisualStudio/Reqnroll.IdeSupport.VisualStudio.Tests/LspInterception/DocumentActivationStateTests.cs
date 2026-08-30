using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// Pure state-machine tests for <see cref="DocumentActivationState"/> (issue #85), including
/// the ordering case where <c>WindowActivated</c> races ahead of <c>didOpen</c>.
/// </summary>
public class DocumentActivationStateTests
{
    private const string Path = @"C:\ws\Features\Calculator.feature";

    [Fact]
    public void WindowActivated_before_any_didOpen_does_not_send_yet()
    {
        var sut = new DocumentActivationState();

        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.None);
    }

    [Fact]
    public void DidOpen_after_a_pending_activation_sends_now()
    {
        var sut = new DocumentActivationState();

        sut.OnWindowActivated(Path);
        var action = sut.OnDidOpen(Path);

        action.Should().Be(DocumentActivationAction.SendNow);
    }

    [Fact]
    public void DidOpen_with_no_prior_activation_does_not_send()
    {
        var sut = new DocumentActivationState();

        sut.OnDidOpen(Path).Should().Be(DocumentActivationAction.None);
    }

    [Fact]
    public void WindowActivated_after_didOpen_sends_now()
    {
        var sut = new DocumentActivationState();

        sut.OnDidOpen(Path);
        var action = sut.OnWindowActivated(Path);

        action.Should().Be(DocumentActivationAction.SendNow);
    }

    [Fact]
    public void A_second_activation_of_an_already_activated_document_is_a_no_op()
    {
        var sut = new DocumentActivationState();

        sut.OnDidOpen(Path);
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.SendNow);

        // Switching away and back to the same still-open tab must not resend.
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.None);
    }

    [Fact]
    public void A_second_activation_while_still_pending_stays_pending()
    {
        var sut = new DocumentActivationState();

        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.None);
        // User alt-tabs away and back before didOpen ever arrives.
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.None);

        sut.OnDidOpen(Path).Should().Be(DocumentActivationAction.SendNow);
    }

    [Fact]
    public void DidClose_resets_state_so_a_reopen_activates_again()
    {
        var sut = new DocumentActivationState();

        sut.OnDidOpen(Path);
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.SendNow);

        sut.OnDidClose(Path);

        sut.OnDidOpen(Path).Should().Be(DocumentActivationAction.None);
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.SendNow);
    }

    [Fact]
    public void DidClose_on_an_unknown_path_is_a_no_op()
    {
        var sut = new DocumentActivationState();

        var act = () => sut.OnDidClose(Path);

        act.Should().NotThrow();
    }

    [Fact]
    public void Paths_are_compared_case_insensitively()
    {
        var sut = new DocumentActivationState();

        sut.OnWindowActivated(Path.ToUpperInvariant());
        var action = sut.OnDidOpen(Path.ToLowerInvariant());

        action.Should().Be(DocumentActivationAction.SendNow);
    }

    [Fact]
    public void Different_documents_track_independently()
    {
        var sut = new DocumentActivationState();
        const string other = @"C:\ws\Features\Other.feature";

        sut.OnDidOpen(Path);
        sut.OnWindowActivated(Path).Should().Be(DocumentActivationAction.SendNow);

        // A second, unrelated file that was never opened must not be affected.
        sut.OnWindowActivated(other).Should().Be(DocumentActivationAction.None);
    }
}
