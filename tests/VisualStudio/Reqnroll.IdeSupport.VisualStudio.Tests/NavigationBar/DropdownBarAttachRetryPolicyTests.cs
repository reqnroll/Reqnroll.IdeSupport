using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.NavigationBar;

/// <summary>
/// Covers when an unattached <see cref="GherkinDropdownBarClient"/> stops retrying, so a client
/// whose view is gone can no longer retry for the rest of the session (issue #78).
/// </summary>
public class DropdownBarAttachRetryPolicyTests
{
    [Fact]
    public void Retries_while_the_view_is_open_and_the_budget_lasts()
    {
        DropdownBarAttachRetryPolicy.AfterFailedAttempt(viewClosed: false, failedAttempts: 1)
            .Should().Be(DropdownBarAttachOutcome.Retry);
    }

    [Fact]
    public void Retries_up_to_the_last_attempt_in_the_budget()
    {
        DropdownBarAttachRetryPolicy.AfterFailedAttempt(false, DropdownBarAttachRetryPolicy.MaxAttachAttempts - 1)
            .Should().Be(DropdownBarAttachOutcome.Retry);
    }

    [Fact]
    public void Stops_once_the_budget_is_spent()
    {
        DropdownBarAttachRetryPolicy.AfterFailedAttempt(false, DropdownBarAttachRetryPolicy.MaxAttachAttempts)
            .Should().Be(DropdownBarAttachOutcome.StopBudgetExhausted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(DropdownBarAttachRetryPolicy.MaxAttachAttempts)]
    public void Stops_as_soon_as_the_view_is_closed(int failedAttempts)
    {
        DropdownBarAttachRetryPolicy.AfterFailedAttempt(viewClosed: true, failedAttempts)
            .Should().Be(DropdownBarAttachOutcome.StopViewClosed);
    }
}
