using System.Threading.Tasks;
using Reqnroll.IdeSupport.Common.Tests.TestHelpers;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem.Settings;

/// <summary>
/// Covers <see cref="ProjectSettingsProvider"/>'s timer-driven retry state machine
/// (<c>StartRetryInitializeTimer</c>/<c>RetryInitializeTimerTick</c>), which has no prior test
/// coverage. Uses the internal test-seam constructor (exposed via <c>InternalsVisibleTo</c>) to
/// shorten the retry delay so the retry loop can be exercised without waiting on the real
/// 5-second interval.
/// </summary>
public class ProjectSettingsProviderTests : IDisposable
{
    private static readonly TimeSpan ShortRetryDelay = TimeSpan.FromMilliseconds(15);

    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ITelemetryService _telemetryService = Substitute.For<ITelemetryService>();
    private readonly IIdeScope _voidIdeScope;
    private readonly IIdeScope _ideScope;
    private readonly IProjectScope _projectScope = Substitute.For<IProjectScope>();
    private readonly ReqnrollProjectSettingsProvider _reqnrollProjectSettingsProvider;
    private readonly List<ProjectSettingsProvider> _createdSuts = new();

    public ProjectSettingsProviderTests()
    {
        _ideScope = Substitute.For<IIdeScope>();
        _ideScope.Logger.Returns(_logger);
        _ideScope.TelemetryService.Returns(_telemetryService);

        _voidIdeScope = Substitute.For<IIdeScope>();
        _voidIdeScope.FileSystem.Returns(new MockFileSystemForTests());
        // GetReqnrollSettings is driven from _projectScope.PackageReferences (below), not from
        // this provider's own captured scope — a VoidProjectScope keeps its config-file/package
        // lookups from touching a real file system (see ReqnrollProjectSettingsProviderTests).
        _reqnrollProjectSettingsProvider = new ReqnrollProjectSettingsProvider(new VoidProjectScope(_voidIdeScope));

        _projectScope.IdeScope.Returns(_ideScope);
        _projectScope.ProjectFullName.Returns(@"C:\proj\Test.csproj");
        _projectScope.GetFeatureFileCount().Returns((int?)0);
    }

    private ProjectSettingsProvider CreateSut(TimeSpan retryDelay)
    {
        var sut = new ProjectSettingsProvider(_projectScope, _reqnrollProjectSettingsProvider, retryDelay);
        _createdSuts.Add(sut);
        return sut;
    }

    /// <summary>Stops every retry timer created by this test's SUTs so none keep firing in the background after the test completes.</summary>
    public void Dispose()
    {
        foreach (var sut in _createdSuts)
            sut.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(5);
        }
    }

    // ── Initial state ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_with_no_packages_leaves_settings_uninitialized()
    {
        _projectScope.PackageReferences.Returns((IEnumerable<NuGetPackageReference>)null!);

        var sut = CreateSut(TimeSpan.FromMinutes(10)); // never actually fires during this test

        sut.GetProjectSettings().IsUninitialized.Should().BeTrue();
    }

    [Fact]
    public void Constructor_with_packages_available_initializes_settings_immediately()
    {
        _projectScope.PackageReferences.Returns(Array.Empty<NuGetPackageReference>());

        var sut = CreateSut(TimeSpan.FromMinutes(10));

        sut.GetProjectSettings().IsUninitialized.Should().BeFalse();
    }

    // ── Failed init triggers a retry ─────────────────────────────────────────────

    [Fact]
    public async Task A_failed_init_retries_and_succeeds_once_packages_become_available()
    {
        var callCount = 0;
        _projectScope.PackageReferences.Returns(_ =>
        {
            callCount++;
            // Fail on the constructor's initial attempt; succeed on the first retry.
            return callCount <= 1 ? null : (IEnumerable<NuGetPackageReference>)Array.Empty<NuGetPackageReference>();
        });

        var sut = CreateSut(ShortRetryDelay);
        sut.GetProjectSettings().IsUninitialized.Should().BeTrue("the first attempt has no packages yet");

        await WaitUntilAsync(() => !sut.GetProjectSettings().IsUninitialized, TimeSpan.FromSeconds(2));

        sut.GetProjectSettings().IsUninitialized.Should().BeFalse();
    }

    // ── Successful init cancels pending retries ──────────────────────────────────

    [Fact]
    public async Task Once_initialization_succeeds_no_further_retries_are_scheduled()
    {
        var callCount = 0;
        _projectScope.PackageReferences.Returns(_ =>
        {
            callCount++;
            return callCount <= 1 ? null : (IEnumerable<NuGetPackageReference>)Array.Empty<NuGetPackageReference>();
        });

        var sut = CreateSut(ShortRetryDelay);
        await WaitUntilAsync(() => !sut.GetProjectSettings().IsUninitialized, TimeSpan.FromSeconds(2));

        var callCountAfterSuccess = callCount;
        // Give any (incorrectly) still-pending timer several delay windows to fire.
        await Task.Delay(ShortRetryDelay + ShortRetryDelay + ShortRetryDelay);

        callCount.Should().Be(callCountAfterSuccess, "the retry loop must stop once initialization succeeds");
    }

    // ── Retries stop after MAX_RETRY_COUNT ───────────────────────────────────────

    [Fact]
    public async Task Retries_stop_after_MAX_RETRY_COUNT_when_packages_never_become_available()
    {
        var callCount = 0;
        _projectScope.PackageReferences.Returns(_ =>
        {
            callCount++;
            return (IEnumerable<NuGetPackageReference>)null!;
        });

        var sut = CreateSut(ShortRetryDelay);

        // 1 initial attempt (constructor) + MAX_RETRY_COUNT retries = the ceiling on calls.
        var expectedMaxCalls = ProjectSettingsProvider.MAX_RETRY_COUNT + 1;
        await WaitUntilAsync(() => callCount >= expectedMaxCalls, TimeSpan.FromSeconds(2));

        var callCountAtCeiling = callCount;
        callCountAtCeiling.Should().Be(expectedMaxCalls);

        // Give any (incorrectly) still-scheduled retry several delay windows to fire.
        await Task.Delay(ShortRetryDelay + ShortRetryDelay + ShortRetryDelay);

        callCount.Should().Be(callCountAtCeiling, "no further retries should fire once MAX_RETRY_COUNT is reached");
        sut.GetProjectSettings().IsUninitialized.Should().BeTrue();
    }

    // ── Thread affinity (#962) ───────────────────────────────────────────────────

    /// <summary>
    /// Mirrors VS: <c>VsProjectScope</c> members call <c>ThreadHelper.ThrowIfNotOnUIThread()</c>
    /// and the provider is constructed on the UI thread. The retry must run on that same thread,
    /// not on the retry timer's thread-pool thread (where the throw would escape and crash the host).
    /// </summary>
    [Fact]
    public async Task Retry_runs_on_the_constructing_threads_synchronization_context()
    {
        using var uiContext = new SingleThreadSynchronizationContext();
        var offThreadAccesses = 0;
        var callCount = 0;

        void EnsureOnUiThread()
        {
            if (!uiContext.IsOnThread)
            {
                Interlocked.Increment(ref offThreadAccesses);
                throw new InvalidOperationException("Simulated RPC_E_WRONG_THREAD: called off the UI thread.");
            }
        }

        _projectScope.GetFeatureFileCount().Returns(_ => { EnsureOnUiThread(); return 0; });
        _projectScope.PlatformTargetName.Returns(_ => { EnsureOnUiThread(); return "AnyCPU"; });
        _projectScope.PackageReferences.Returns(_ =>
        {
            EnsureOnUiThread();
            callCount++;
            return callCount <= 1 ? null : (IEnumerable<NuGetPackageReference>)Array.Empty<NuGetPackageReference>();
        });

        var sut = await uiContext.RunAsync(() => CreateSut(ShortRetryDelay));
        sut.GetProjectSettings().IsUninitialized.Should().BeTrue("the first attempt has no packages yet");

        await WaitUntilAsync(() => !sut.GetProjectSettings().IsUninitialized, TimeSpan.FromSeconds(2));

        offThreadAccesses.Should().Be(0, "the retry must not touch the project scope off the UI thread");
        sut.GetProjectSettings().IsUninitialized.Should().BeFalse();
    }

    [Fact]
    public async Task An_exception_during_a_retry_is_logged_and_does_not_escape_the_timer_thread()
    {
        var callCount = 0;
        _projectScope.PackageReferences.Returns(_ =>
        {
            callCount++;
            if (callCount == 1)
                return null;
            if (callCount == 2)
                throw new InvalidOperationException("Simulated project-system failure during retry.");
            return (IEnumerable<NuGetPackageReference>)Array.Empty<NuGetPackageReference>();
        });

        var sut = CreateSut(ShortRetryDelay);

        await WaitUntilAsync(() => !sut.GetProjectSettings().IsUninitialized, TimeSpan.FromSeconds(2));

        _logger.Received().Log(Arg.Is<LogMessage>(m => m.Exception is InvalidOperationException));
        sut.GetProjectSettings().IsUninitialized.Should().BeFalse("a failed retry must still schedule the next one");
    }

    /// <summary>A synchronization context that runs every posted callback on one dedicated thread, standing in for the VS UI thread.</summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true, Name = "Simulated UI thread" };
            _thread.Start();
        }

        public bool IsOnThread => Thread.CurrentThread == _thread;

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
                _queue.Add((d, state));
        }

        public Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            }, null);
            return tcs.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
