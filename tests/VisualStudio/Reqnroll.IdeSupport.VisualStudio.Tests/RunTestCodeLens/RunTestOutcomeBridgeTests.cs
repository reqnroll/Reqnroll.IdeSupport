using System.Reflection;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Direct unit tests for the three stages <see cref="RunTestOutcomeBridge.TryGetOutcomeAsync"/>
/// splits into (issue #590): <b>invoke</b> (<see cref="RunTestOutcomeBridge.InvokeGetTestOutcomeAsync"/>)
/// and <b>map</b> (<see cref="RunTestOutcomeBridge.ParseOutcome"/>) are tested here with substituted
/// handles instead of the real (unsupported, internal) VS test-window API; <b>acquire</b>
/// (<c>GetOrCreateProxyAsync</c>) does real reflection against that API and is deliberately not
/// exercised end-to-end here — see the class's own remarks on why a shape-change failure can only
/// be observed live. The shared failure-classification dispatch (<see cref="RunTestOutcomeBridge.HandleFailure"/>)
/// is tested directly instead: it is what actually latches <c>_unavailable</c>, independent of
/// which stage's exception reaches it.
/// </summary>
public class RunTestOutcomeBridgeTests : IDisposable
{
    // All fields RunTestOutcomeBridge touches are static (issue #590's own remarks: a stable
    // per-process cached connection), so every test resets them on both sides of the run to avoid
    // leaking state into whichever test happens to run next.
    public RunTestOutcomeBridgeTests() => RunTestOutcomeBridge.ResetStateForTests();
    public void Dispose() => RunTestOutcomeBridge.ResetStateForTests();

    // A plain object stands in for the real TestMethodIdentifier: InvokeGetTestOutcomeAsync's
    // testMethod parameter is deliberately widened to object (see its own remarks) precisely so
    // this substitution works without pulling the VS-install-version-pinned test-window assembly
    // into this unit test.
    private static object MakeTestMethod() => new();

    // ── Map stage: ParseOutcome ─────────────────────────────────────────────

    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    [InlineData("Skipped")]
    public void ParseOutcome_maps_a_recognized_name_to_its_enum_value(string outcomeName)
    {
        // RunTestOutcome is internal, so the expected value is looked up here rather than passed
        // as theory data -- a public [Theory] method can't declare an internal parameter type.
        var expected = outcomeName switch
        {
            "Passed" => RunTestOutcome.Passed,
            "Failed" => RunTestOutcome.Failed,
            "Skipped" => RunTestOutcome.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(outcomeName)),
        };

        RunTestOutcomeBridge.ParseOutcome(outcomeName).Should().Be(expected);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("NotFound")]
    [InlineData("SomeFutureOutcomeValue")]
    [InlineData(null)]
    public void ParseOutcome_maps_anything_unrecognized_to_null_no_glyph(string? outcomeName)
        => RunTestOutcomeBridge.ParseOutcome(outcomeName).Should().BeNull();

    // ── Invoke stage: InvokeGetTestOutcomeAsync, with substituted handles ────

    private sealed class FakeTestOutcomeService
    {
        public Task<string> GetOutcome(Guid dataPointId, object testMethod, CancellationToken ct)
            => Task.FromResult("Passed");

        public Task? ReturnsNullTask(Guid dataPointId, object testMethod, CancellationToken ct) => null;

        public Task ReturnsATaskWithNoResultProperty(Guid dataPointId, object testMethod, CancellationToken ct)
            => Task.CompletedTask; // plain (non-generic) Task -- no Result property, mirroring a reshaped API

        public Task<string> ThrowsSynchronously(Guid dataPointId, object testMethod, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private static MethodInfo Method(string name) =>
        typeof(FakeTestOutcomeService).GetMethod(name) ?? throw new MissingMethodException(name);

    [Fact]
    public async Task InvokeGetTestOutcomeAsync_returns_the_awaited_result_value()
    {
        var outcome = await RunTestOutcomeBridge.InvokeGetTestOutcomeAsync(
            new FakeTestOutcomeService(), Method(nameof(FakeTestOutcomeService.GetOutcome)),
            MakeTestMethod(), CancellationToken.None);

        outcome.Should().Be("Passed");
    }

    [Fact]
    public async Task InvokeGetTestOutcomeAsync_returns_null_when_the_invoked_method_returns_a_null_task()
    {
        var outcome = await RunTestOutcomeBridge.InvokeGetTestOutcomeAsync(
            new FakeTestOutcomeService(), Method(nameof(FakeTestOutcomeService.ReturnsNullTask)),
            MakeTestMethod(), CancellationToken.None);

        outcome.Should().BeNull();
    }

    [Fact]
    public async Task InvokeGetTestOutcomeAsync_throws_MissingMemberException_when_the_result_task_has_no_Result_property()
    {
        // A plain (non-generic) Task has no Result property -- the shape TryGetOutcomeAsync's
        // catch-all treats as a permanent API shape change via HandleFailure.
        var act = () => RunTestOutcomeBridge.InvokeGetTestOutcomeAsync(
            new FakeTestOutcomeService(), Method(nameof(FakeTestOutcomeService.ReturnsATaskWithNoResultProperty)),
            MakeTestMethod(), CancellationToken.None);

        await act.Should().ThrowAsync<MissingMemberException>();
    }

    [Fact]
    public async Task InvokeGetTestOutcomeAsync_propagates_an_exception_thrown_by_the_invoked_method()
    {
        var act = () => RunTestOutcomeBridge.InvokeGetTestOutcomeAsync(
            new FakeTestOutcomeService(), Method(nameof(FakeTestOutcomeService.ThrowsSynchronously)),
            MakeTestMethod(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    // ── Failure classification/dispatch: HandleFailure ──────────────────────

    [Fact]
    public void HandleFailure_latches_unavailable_for_a_TypeLoadException()
    {
        RunTestOutcomeBridge.HandleFailure(new TypeLoadException("shape changed"), "GetOrCreateProxyAsync");

        RunTestOutcomeBridge.IsUnavailableForTests.Should().BeTrue();
    }

    [Fact]
    public void HandleFailure_latches_unavailable_for_a_MissingMemberException()
    {
        RunTestOutcomeBridge.HandleFailure(new MissingMemberException("member gone"), "GetOrCreateProxyAsync");

        RunTestOutcomeBridge.IsUnavailableForTests.Should().BeTrue();
    }

    [Fact]
    public void HandleFailure_does_not_latch_unavailable_for_a_transient_exception()
    {
        // The whole point of distinguishing the two (this type's own remarks): a dropped
        // ServiceHub connection right after a fresh VS launch must get a clean retry next call,
        // not a permanent, session-wide "no glyph ever again" outcome.
        RunTestOutcomeBridge.HandleFailure(new InvalidOperationException("dropped connection"), "GetOrCreateProxyAsync");

        RunTestOutcomeBridge.IsUnavailableForTests.Should().BeFalse();
    }

    [Fact]
    public void HandleFailure_never_throws_regardless_of_the_exception_it_is_classifying()
    {
        var act = () =>
        {
            RunTestOutcomeBridge.HandleFailure(new TypeLoadException(), "step");
            RunTestOutcomeBridge.HandleFailure(new MissingMemberException(), "step");
            RunTestOutcomeBridge.HandleFailure(new InvalidOperationException(), "step");
            RunTestOutcomeBridge.HandleFailure(new NullReferenceException(), "step");
        };

        act.Should().NotThrow();
    }
}
