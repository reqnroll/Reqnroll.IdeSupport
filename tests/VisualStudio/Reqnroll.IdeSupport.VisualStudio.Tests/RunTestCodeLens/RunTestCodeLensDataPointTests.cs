using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.TestWindow;
using NSubstitute;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Coverage for issue #700's fix: the Run CodeLens pass/fail glyph used to be fetched exactly once,
/// at data-point creation time, and then frozen forever — a completed test run had no way to tell
/// this data point to re-fetch. <see cref="RunTestOutcomeBridge.TestChanged"/> is the new signal
/// (fired by VS's own test-outcome push notification, see that type's remarks); these tests cover
/// the consuming half — that <see cref="RunTestCodeLensDataPoint"/> actually subscribes, filters to
/// its own resolved methods, and unsubscribes on <see cref="IDisposable.Dispose"/>.
/// </summary>
public class RunTestCodeLensDataPointTests : IDisposable
{
    // RunTestOutcomeBridge.TestChanged is static -- reset on both sides so one test's subscription
    // can never leak into the next.
    public RunTestCodeLensDataPointTests() => RunTestOutcomeBridge.ResetStateForTests();
    public void Dispose() => RunTestOutcomeBridge.ResetStateForTests();

    private const string FileUri = "file:///Features/Sample.feature";
    private const int Line = 12;
    private static readonly TestMethodIdentifier ResolvedMethod = new("asm.dll", "SampleFeature.AScenario", "SampleFeature", "AScenario");

    private static ICodeLensCallbackService CreateCallbackServiceReturning(params RunTestTargetEntry[] entries)
    {
        var callbackService = Substitute.For<ICodeLensCallbackService>();
        callbackService
            .InvokeAsync<IReadOnlyList<RunTestTargetEntry>>(
                Arg.Any<IAsyncCodeLensDataPoint>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<object>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RunTestTargetEntry>>(entries));
        return callbackService;
    }

    private static RunTestCodeLensDataPoint CreateSut(ICodeLensCallbackService callbackService) =>
        new(new CodeLensDescriptor(), callbackService, FileUri, Line, Substitute.For<IIdeSupportLogger>());

    [Fact]
    public async Task GetDataAsync_subscribes_and_a_matching_TestChanged_notification_raises_InvalidatedAsync()
    {
        var callbackService = CreateCallbackServiceReturning(
            new RunTestTargetEntry(Line, ResolvedMethod.OutputFilePath, ResolvedMethod.ManagedType, ResolvedMethod.ManagedMethod));
        using var sut = CreateSut(callbackService);
        await sut.GetDataAsync(new CodeLensDescriptorContext(null, new Dictionary<object, object>()), CancellationToken.None);

        var invalidated = new TaskCompletionSource<bool>();
        sut.InvalidatedAsync += (_, _) => { invalidated.TrySetResult(true); return Task.CompletedTask; };

        await RaiseTestChanged(ResolvedMethod);

        var completed = await Task.WhenAny(invalidated.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(invalidated.Task, "a TestChanged notification for this line's own resolved method should invalidate the lens");
    }

    [Fact]
    public async Task GetDataAsync_ignores_a_TestChanged_notification_for_an_unrelated_method()
    {
        var callbackService = CreateCallbackServiceReturning(
            new RunTestTargetEntry(Line, ResolvedMethod.OutputFilePath, ResolvedMethod.ManagedType, ResolvedMethod.ManagedMethod));
        using var sut = CreateSut(callbackService);
        await sut.GetDataAsync(new CodeLensDescriptorContext(null, new Dictionary<object, object>()), CancellationToken.None);

        var invalidated = new TaskCompletionSource<bool>();
        sut.InvalidatedAsync += (_, _) => { invalidated.TrySetResult(true); return Task.CompletedTask; };

        var unrelated = new TestMethodIdentifier("other.dll", "Other.Method", "Other", "Method");
        await RaiseTestChanged(unrelated);

        var completed = await Task.WhenAny(invalidated.Task, Task.Delay(TimeSpan.FromMilliseconds(300)));
        completed.Should().NotBe(invalidated.Task, "a notification about a different test method should not invalidate this line's lens");
    }

    [Fact]
    public async Task Dispose_unsubscribes_so_a_later_TestChanged_notification_is_a_no_op()
    {
        var callbackService = CreateCallbackServiceReturning(
            new RunTestTargetEntry(Line, ResolvedMethod.OutputFilePath, ResolvedMethod.ManagedType, ResolvedMethod.ManagedMethod));
        var sut = CreateSut(callbackService);
        await sut.GetDataAsync(new CodeLensDescriptorContext(null, new Dictionary<object, object>()), CancellationToken.None);

        var invalidated = new TaskCompletionSource<bool>();
        sut.InvalidatedAsync += (_, _) => { invalidated.TrySetResult(true); return Task.CompletedTask; };

        sut.Dispose();
        await RaiseTestChanged(ResolvedMethod);

        var completed = await Task.WhenAny(invalidated.Task, Task.Delay(TimeSpan.FromMilliseconds(300)));
        completed.Should().NotBe(invalidated.Task, "a disposed data point must not still react to TestChanged");
    }

    [Fact]
    public async Task Concurrent_GetDataAsync_calls_subscribe_exactly_once()
    {
        // Fresh-eyes review (#701): the subscribe-once guard was a plain check-then-act on a bool.
        // GetDataAsync's own remarks already say VS can call it more than once per instance
        // (incremental refreshes); if two such calls overlap, both could see "not yet subscribed" and
        // both subscribe, double-firing InvalidatedAsync per notification. Firing many concurrent
        // calls raises the odds of exposing that interleaving if the guard ever regresses back to a
        // plain bool -- with the fix in place (Interlocked.CompareExchange), the outcome below is
        // deterministic regardless of interleaving.
        var callbackService = CreateCallbackServiceReturning(
            new RunTestTargetEntry(Line, ResolvedMethod.OutputFilePath, ResolvedMethod.ManagedType, ResolvedMethod.ManagedMethod));
        using var sut = CreateSut(callbackService);

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => sut.GetDataAsync(new CodeLensDescriptorContext(null, new Dictionary<object, object>()), CancellationToken.None)));

        var invalidationCount = 0;
        sut.InvalidatedAsync += (_, _) => { Interlocked.Increment(ref invalidationCount); return Task.CompletedTask; };

        await RaiseTestChanged(ResolvedMethod);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        invalidationCount.Should().Be(1, "GetDataAsync running concurrently must still subscribe exactly once, however many times it's called");
    }

    private static Task RaiseTestChanged(TestMethodIdentifier testMethod) =>
        new RunTestOutcomeBridge.RunTestOutcomeCallbackTarget().OnTestChangedAsync(testMethod);
}
