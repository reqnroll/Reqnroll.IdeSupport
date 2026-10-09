using NuGet.VisualStudio.Contracts;
using Reqnroll.IdeSupport.VisualStudio.ProjectSystem;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.ProjectSystem;

/// <summary>
/// <see cref="VsUtils.QueryInstalledNuGetPackagesAsync"/>: the bounded NuGet brokered-service query
/// behind <see cref="VsUtils.GetInstalledNuGetPackages"/> (issue #1031). The RPC is supplied as a
/// delegate so a NuGet service that never answers can be simulated without a VS host.
/// </summary>
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks -- no JoinableTaskFactory/UI thread in these tests
public class InstalledNuGetPackagesQueryTests
{
    // Generous guard so a hang shows up as a test failure instead of a stuck test run.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_NuGet_service_that_never_answers_is_reported_as_not_ready_within_the_timeout()
    {
        var neverCompletes = new TaskCompletionSource<InstalledPackagesResult>();

        var query = VsUtils.QueryInstalledNuGetPackagesAsync(_ => neverCompletes.Task, ShortTimeout, CancellationToken.None);
        await ShouldCompleteWithinGuardAsync(query);

        var exception = await FluentActions.Awaiting(() => query).Should().ThrowAsync<NuGetProjectNotReadyException>();
        exception.Which.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task The_RPC_is_given_a_token_that_is_cancelled_when_the_timeout_fires()
    {
        var neverCompletes = new TaskCompletionSource<InstalledPackagesResult>();
        CancellationToken passedToken = default;

        var query = VsUtils.QueryInstalledNuGetPackagesAsync(ct =>
        {
            passedToken = ct;
            return neverCompletes.Task;
        }, ShortTimeout, CancellationToken.None);
        await Task.WhenAny(query, Task.Delay(HangGuard));

        passedToken.CanBeCanceled.Should().BeTrue();
        passedToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates_instead_of_being_reported_as_not_ready()
    {
        var neverCompletes = new TaskCompletionSource<InstalledPackagesResult>();
        using var callerCancellation = new CancellationTokenSource();

        var query = VsUtils.QueryInstalledNuGetPackagesAsync(_ => neverCompletes.Task, HangGuard, callerCancellation.Token);
        callerCancellation.Cancel();
        await ShouldCompleteWithinGuardAsync(query);

        await FluentActions.Awaiting(() => query).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_successful_result_returns_the_packages()
    {
        var package = NuGetContractsFactory.CreateNuGetInstalledPackage("Reqnroll", "[2.0.0, )", "2.0.0", "C:\\packages\\reqnroll\\2.0.0", true);
        var result = NuGetContractsFactory.CreateInstalledPackagesResult(InstalledPackageResultStatus.Successful, new[] { package });

        var packages = await VsUtils.QueryInstalledNuGetPackagesAsync(_ => Task.FromResult(result), ShortTimeout, CancellationToken.None);

        packages.Should().ContainSingle().Which.Id.Should().Be("Reqnroll");
    }

    [Fact]
    public async Task A_ProjectNotReady_result_is_reported_as_not_ready_without_a_timeout()
    {
        var result = NuGetContractsFactory.CreateInstalledPackagesResult(InstalledPackageResultStatus.ProjectNotReady, Array.Empty<NuGetInstalledPackage>());

        var exception = await FluentActions
            .Awaiting(() => VsUtils.QueryInstalledNuGetPackagesAsync(_ => Task.FromResult(result), ShortTimeout, CancellationToken.None))
            .Should().ThrowAsync<NuGetProjectNotReadyException>();

        exception.Which.TimedOut.Should().BeFalse();
    }

    private static async Task ShouldCompleteWithinGuardAsync(Task query)
    {
        var finished = await Task.WhenAny(query, Task.Delay(HangGuard));
        finished.Should().BeSameAs(query, "the query must not wait forever on a NuGet service that never answers");
    }
}
