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
}
