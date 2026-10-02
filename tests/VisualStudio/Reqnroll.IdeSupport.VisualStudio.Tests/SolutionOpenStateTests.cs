using AwesomeAssertions;
using Microsoft.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Covers the decision rule <see cref="SolutionOpenState"/> applies to a raw
/// <c>IVsSolution.GetProperty</c> result (issue #774). The COM call itself needs the UI thread and a
/// live <c>IVsSolution</c>, so it is not covered here.
/// </summary>
public class SolutionOpenStateTests
{
    [Fact]
    public void True_when_the_call_succeeds_and_reports_open()
    {
        SolutionOpenState.FromPropertyResult(VSConstants.S_OK, true)
            .Should().BeTrue();
    }

    [Fact]
    public void False_when_the_call_succeeds_but_reports_closed()
    {
        SolutionOpenState.FromPropertyResult(VSConstants.S_OK, false)
            .Should().BeFalse();
    }

    [Fact]
    public void False_when_the_call_fails_even_if_the_value_looks_true()
    {
        // Issue #774: this is the case that mattered. Reading a nonexistent VSPROPID fails the call
        // but can still hand back a stale or default out value, so the HRESULT must be checked too.
        SolutionOpenState.FromPropertyResult(VSConstants.E_INVALIDARG, true)
            .Should().BeFalse();
    }

    [Fact]
    public void False_when_the_property_value_is_not_a_boolean()
    {
        SolutionOpenState.FromPropertyResult(VSConstants.S_OK, null)
            .Should().BeFalse();
    }
}
