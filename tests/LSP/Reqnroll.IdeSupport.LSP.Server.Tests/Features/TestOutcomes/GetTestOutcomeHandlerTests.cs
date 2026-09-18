using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// <see cref="GetTestOutcomeHandler"/> — the server-side outcome lookup for <c>reqnroll/testOutcomes/getOutcome</c>,
/// moved here from the Visual Studio-only <c>RunTestCodeLensCallbackListener</c> (LSP-server outcome
/// pipeline refactor) so every IDE gets the same staleness/trust rules. Covers the end-to-end
/// aged-out-vs-recent behaviour (self-caught fix during the original implementation: an aged-out entry
/// must come back as "not found", not "stale but populated", so the client falls through to its own
/// reflection bridge instead of a dead end) and the step-trace-to-DTO mapping (implementation plan §2).
/// </summary>
public class GetTestOutcomeHandlerTests : IDisposable
{
    /// <summary>Used only by the pure store/mapping tests below, which never touch freshness — no real file needed.</summary>
    private const string Source = @"C:\repo\Specs\bin\Debug\net8.0\Specs.dll";
    private const string Type = "Specs.Features.CalculatorFeature";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reqnroll-get-outcome-handler-tests", Guid.NewGuid().ToString("N"));
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A real file on disk, so freshness checks against its write time behave as "not rebuilt since".</summary>
    private string NewContainer()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "Specs.dll");
        File.WriteAllText(path, "not a real assembly, just needs a write time");
        return path;
    }

    private static TestResultRecord Result(string source, string method, string display, TestOutcomeKind outcome, string? stdout, string? error = null)
        => new("run-1", source, Type, method + "()", $"{Type}.{method}", display, outcome, 1234.5, error, null, stdout, false);

    private static GetTestOutcomeParams Params(string source, string method)
        => new() { AssemblyPath = source, TypeFullName = Type, MethodName = method };

    [Fact]
    public async Task HandleAsync_returns_not_found_for_an_aged_out_entry_instead_of_a_stale_one()
    {
        var container = NewContainer();
        var store = new TestOutcomeStore();
        var handler = new GetTestOutcomeHandler(store, _logger);
        var oldWhen = DateTime.UtcNow - GetTestOutcomeHandler.MaxTrustedAge - TimeSpan.FromMinutes(5);
        store.Record(new TestResultRecord("run-1", container, Type, "So23()", $"{Type}.So23", "row", TestOutcomeKind.Passed, 1, null, null, null, false), nowUtc: oldWhen);

        var response = await handler.HandleAsync(Params(container, "So23"), CancellationToken.None);

        // Not Found:true-with-stale-data — genuinely absent, so the caller falls through to the
        // bridge exactly as it does for a method the store has never heard of at all.
        response.Found.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_returns_the_entry_for_a_recent_one()
    {
        var container = NewContainer();
        var store = new TestOutcomeStore();
        var handler = new GetTestOutcomeHandler(store, _logger);
        store.Record(new TestResultRecord("run-1", container, Type, "So23()", $"{Type}.So23", "row", TestOutcomeKind.Passed, 1, null, null, null, false));

        var response = await handler.HandleAsync(Params(container, "So23"), CancellationToken.None);

        response.Found.Should().BeTrue();
        response.Aggregate.Should().Be("Passed");
        response.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_returns_not_found_for_a_method_the_store_never_heard_of()
    {
        var container = NewContainer();
        var store = new TestOutcomeStore();
        var handler = new GetTestOutcomeHandler(store, _logger);

        var response = await handler.HandleAsync(Params(container, "Unknown"), CancellationToken.None);

        response.Found.Should().BeFalse();
    }

    [Fact]
    public void Store_parses_the_step_trace_and_exposes_the_failing_step()
    {
        const string trace =
            "TestContext Messages:\n" +
            "Given the first number is 1\n" +
            "-> done: CalculatorSteps.GivenTheFirstNumberIs(1) (0.0s)\n" +
            "When the calculation explodes\n" +
            "-> error: deliberate failure in the middle step (0.0s)\n" +
            "Then the result should be 2\n" +
            "-> skipped because of previous errors\n";
        var store = new TestOutcomeStore();
        store.Record(Result(Source, "AStepInTheMiddleFails", "A step in the middle fails", TestOutcomeKind.Failed, trace, "deliberate failure in the middle step"));

        var row = store.TryGet(Source, Type, "AStepInTheMiddleFails")!.Rows.Single();
        row.Steps.Should().HaveCount(3);
        row.FailedStep.Should().NotBeNull();
        row.FailedStep!.Index.Should().Be(1, "the middle step threw; the trace, unlike the stack trace, says so");
        row.FailedStep.StepText.Should().Be("When the calculation explodes");
    }

    [Fact]
    public void Rows_without_a_trace_have_no_steps_and_no_failed_step()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(Source, "Plain", "Plain", TestOutcomeKind.Failed, stdout: null, error: "boom"));

        var row = store.TryGet(Source, Type, "Plain")!.Rows.Single();
        row.Steps.Should().BeEmpty();
        row.FailedStep.Should().BeNull();
    }

    [Fact]
    public void ToResponse_carries_the_failed_step_per_row()
    {
        var store = new TestOutcomeStore();
        store.Record(Result(Source, "AddingRows", "Adding rows(1,2,3,2)", TestOutcomeKind.Passed,
            "Given the first number is 1\n-> done: S.G(1) (0.0s)\nThen the result should be 3\n-> done: S.T(3) (0.0s)\n"));
        store.Record(Result(Source, "AddingRows", "Adding rows(5,5,11,4)", TestOutcomeKind.Failed,
            "Given the first number is 5\n-> done: S.G(5) (0.0s)\nThen the result should be 11\n-> error: Assert.AreEqual failed. Expected:<11>. Actual:<10>.  (0.0s)\n",
            "Assert.AreEqual failed. Expected:<11>. Actual:<10>."));

        var response = GetTestOutcomeHandler.ToResponse(store.TryGet(Source, Type, "AddingRows")!, isStale: false);

        response.Aggregate.Should().Be("Failed");
        response.Rows.Should().HaveCount(2);
        var passed = response.Rows.Single(r => r.Outcome == "Passed");
        passed.StepCount.Should().Be(2);
        passed.FailedStepIndex.Should().BeNull();
        passed.FailedStepText.Should().BeNull();
        var failed = response.Rows.Single(r => r.Outcome == "Failed");
        failed.StepCount.Should().Be(2);
        failed.FailedStepIndex.Should().Be(1);
        failed.FailedStepText.Should().Be("Then the result should be 11");
        failed.FailedStepOutcome.Should().Be("Error");
        failed.ErrorMessage.Should().StartWith("Assert.AreEqual failed");
        failed.DurationMs.Should().Be(1234.5);
    }
}
