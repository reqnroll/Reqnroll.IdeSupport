using MediatR;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// Direct unit tests for <see cref="MembershipIndex"/> in isolation, constructing it standalone
/// (no <see cref="LspWorkspaceScopeManager"/>, no <see cref="LspIdeScope"/>, no disk I/O — paths
/// used here never need to exist) rather than driving it through
/// <see cref="ILspWorkspaceScopeManager"/> as <c>LspWorkspaceScopeManagerMembershipTests.cs</c> does.
/// <para>
/// This complements — not replaces — the existing indirect coverage: those tests confirm the
/// manager wires the index in correctly (folder-prefix fallback, workspace-scope Pending/Unowned
/// classification); these confirm the index's own bookkeeping (baseline/delta application,
/// path-key normalisation, the deferred-rescan flag, and the exact shape of the published
/// notifications (<see cref="BindingRegistryReplacedNotification"/>,
/// <see cref="BindingRegistryPatchedNotification"/>,
/// <see cref="ProjectBindingFilesRemovedNotification"/>) without those extra moving parts.
/// </para>
/// </summary>
public class MembershipIndexTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IIdeScope _ideScope = Substitute.For<IIdeScope>();

    // Test-controlled stand-in for the lifecycle side's `_scopes` registry: register a project
    // to make it "live" (as if reqnroll/projectLoaded had already registered it), or leave it
    // unregistered to simulate the baseline racing ahead of that notification (issue #48).
    private readonly Dictionary<ProjectKey, LspReqnrollProject> _liveProjects = new();

    private MembershipIndex CreateSut() =>
        new(_logger, _mediator, key => _liveProjects.GetValueOrDefault(key));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private LspReqnrollProject MakeProject(string projectFile, string tfm = ".NETCoreApp,Version=v8.0")
    {
        var info = new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder        = Path.GetDirectoryName(projectFile) ?? string.Empty,
            ProjectFile            = projectFile,
            ProjectFolder          = Path.GetDirectoryName(projectFile) ?? string.Empty,
            OutputAssemblyPath     = Path.Combine(Path.GetDirectoryName(projectFile) ?? string.Empty, "bin", "My.dll"),
            TargetFrameworkMoniker = tfm
        };
        return new LspReqnrollProject(info, _ideScope);
    }

    /// <summary>Registers <paramref name="project"/> as "live" so <c>findProjectByKey</c> resolves it — mirrors <c>reqnroll/projectLoaded</c> having already registered it.</summary>
    private void Register(LspReqnrollProject project) =>
        _liveProjects[new ProjectKey(MembershipIndex.NormaliseFilePath(project.ProjectFullName), project.TargetFrameworkMoniker)] = project;

    /// <summary>
    /// Polls until <see cref="_mediator"/> has received at least one <c>Publish</c> call.
    /// <see cref="MembershipIndex"/> now dispatches its notification via
    /// <see cref="FireAndForgetExtensions"/> (issue #477) so it genuinely runs on a thread-pool
    /// thread rather than inline — by design, since the whole point of the fix is that the
    /// caller (here, the awaited <c>HandleProjectFilesAsync</c> call) no longer blocks on it.
    /// That means it may not have happened yet the instant <c>HandleProjectFilesAsync</c>
    /// returns, so tests that assert on it (or need it flushed before <c>ClearReceivedCalls</c>)
    /// must wait for it explicitly instead of asserting immediately.
    /// </summary>
    private Task WaitForPublishAsync(TimeSpan? timeout = null) => WaitForPublishAsync(atLeast: 1, timeout);

    /// <summary>
    /// Overload for a delta that removes a binding-role file (issue #577): that path now
    /// publishes <em>two</em> notifications in sequence within the same background continuation
    /// (<see cref="ProjectBindingFilesRemovedNotification"/> then <see cref="BindingRegistryPatchedNotification"/>)
    /// rather than the one combined notification it used to. Both land essentially back-to-back
    /// since the substitute's <c>Publish</c> completes synchronously, but a poll landing in the
    /// narrow window between them would otherwise see the first and return early.
    /// </summary>
    private async Task WaitForPublishAsync(int atLeast, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (_mediator.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMediator.Publish)) >= atLeast)
                return;
            await Task.Delay(5);
        }
    }

    private static ReqnrollProjectFilesParams BaselineParams(
        string projectFile, string tfm, params (string path, ProjectFileRole role)[] entries)
        => new()
        {
            ProjectFile            = projectFile,
            TargetFrameworkMoniker = tfm,
            Kind                   = ProjectFilesKind.Baseline,
            Files                  = entries
                .Select(e => new ProjectFileEntry { Path = e.path, Role = e.role, Added = true })
                .ToArray()
        };

    private static ReqnrollProjectFilesParams DeltaParams(
        string projectFile, string tfm, params (string path, ProjectFileRole role, bool added)[] entries)
        => new()
        {
            ProjectFile            = projectFile,
            TargetFrameworkMoniker = tfm,
            Kind                   = ProjectFilesKind.Delta,
            Files                  = entries
                .Select(e => new ProjectFileEntry { Path = e.path, Role = e.role, Added = e.added })
                .ToArray()
        };

    private static DocumentUri UriFor(string path) => DocumentUri.FromFileSystemPath(path);

    // ── Baseline: CRUD ────────────────────────────────────────────────────────

    [Fact]
    public async Task Baseline_populates_GetProjectsForUri_and_IsPathOwned()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var feature = @"C:\Proj\A.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (feature, ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.IsPathOwned(feature).Should().BeTrue();
        sut.GetProjectsForUri(UriFor(feature)).Should().ContainSingle().Which.Should().BeSameAs(project);
    }

    [Fact]
    public async Task Baseline_sets_HasBaseline_and_HasBaselineForProject()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.HasBaselineForProject(project).Should().BeTrue();
        sut.HasBaseline(new ProjectKey(
                MembershipIndex.NormaliseFilePath(project.ProjectFullName), project.TargetFrameworkMoniker))
            .Should().BeTrue();
    }

    [Fact]
    public void HasBaselineForProject_is_false_before_any_baseline()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");

        sut.HasBaselineForProject(project).Should().BeFalse();
    }

    [Fact]
    public void GetProjectsForUri_returns_empty_before_any_baseline()
    {
        var sut = CreateSut();

        sut.GetProjectsForUri(UriFor(@"C:\Proj\A.feature")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetIndexedFeatureFiles_and_GetBindingFilePathsForProject_partition_by_role()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var feature = @"C:\Proj\A.feature";
        var binding = @"C:\Proj\Steps.cs";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (feature, ProjectFileRole.Feature),
                (binding, ProjectFileRole.Binding)),
            CancellationToken.None);

        sut.GetIndexedFeatureFiles(project).Should()
            .ContainSingle().Which.Should().Be(MembershipIndex.NormaliseFilePath(feature));
        sut.GetBindingFilePathsForProject(project).Should()
            .ContainSingle().Which.Should().Be(MembershipIndex.NormaliseFilePath(binding));
    }

    [Fact]
    public async Task Second_baseline_replaces_the_projects_prior_contribution()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var oldPath = @"C:\Proj\Old.feature";
        var freshPath = @"C:\Proj\Fresh.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (oldPath, ProjectFileRole.Feature)),
            CancellationToken.None);
        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (freshPath, ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.IsPathOwned(oldPath).Should().BeFalse("the first baseline's entries must be cleared by the second");
        sut.IsPathOwned(freshPath).Should().BeTrue();
    }

    [Fact]
    public async Task Baseline_from_a_second_project_does_not_disturb_the_first_projects_entries()
    {
        var sut = CreateSut();
        var p1 = MakeProject(@"C:\Proj1\My.csproj");
        var p2 = MakeProject(@"C:\Proj2\Other.csproj");
        Register(p1);
        Register(p2);
        var p1File = @"C:\Proj1\A.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFullName, p1.TargetFrameworkMoniker,
                (p1File, ProjectFileRole.Feature)),
            CancellationToken.None);
        await sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFullName, p2.TargetFrameworkMoniker,
                (@"C:\Proj2\B.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.GetProjectsForUri(UriFor(p1File)).Should().ContainSingle().Which.Should().BeSameAs(p1);
    }

    [Fact]
    public async Task GetProjectsForUri_returns_every_owner_of_a_shared_file()
    {
        var sut = CreateSut();
        var p1 = MakeProject(@"C:\Proj1\My.csproj");
        var p2 = MakeProject(@"C:\Proj2\Other.csproj");
        Register(p1);
        Register(p2);
        var shared = @"C:\Shared\Linked.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(p1.ProjectFullName, p1.TargetFrameworkMoniker, (shared, ProjectFileRole.Feature)),
            CancellationToken.None);
        await sut.HandleProjectFilesAsync(
            BaselineParams(p2.ProjectFullName, p2.TargetFrameworkMoniker, (shared, ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.GetProjectsForUri(UriFor(shared)).Should().HaveCount(2)
            .And.Contain(p1).And.Contain(p2);
    }

    // ── Path-key normalisation ────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_is_case_insensitive_on_windows_style_paths()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\Steps.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.IsPathOwned(@"c:\proj\STEPS.feature").Should().BeTrue();
        sut.GetProjectsForUri(UriFor(@"c:\PROJ\steps.FEATURE")).Should().ContainSingle();
    }

    [Fact]
    public async Task Lookup_normalises_redundant_path_segments_to_the_same_key()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\Sub\..\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        // Equivalent path with the ".." segment resolved away must hit the same index entry.
        sut.IsPathOwned(@"C:\Proj\A.feature").Should().BeTrue();
    }

    // ── Delta ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delta_is_dropped_when_no_baseline_has_been_received()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var path = @"C:\Proj\Late.feature";

        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (path, ProjectFileRole.Feature, true)),
            CancellationToken.None);

        sut.IsPathOwned(path).Should().BeFalse();
        _ = _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
    }

    [Fact]
    public async Task Delta_adds_a_file_after_baseline()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var existing = @"C:\Proj\Existing.feature";
        var added = @"C:\Proj\Added.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (existing, ProjectFileRole.Feature)),
            CancellationToken.None);
        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (added, ProjectFileRole.Feature, true)),
            CancellationToken.None);

        sut.IsPathOwned(added).Should().BeTrue();
    }

    [Fact]
    public async Task Delta_removes_a_file_after_baseline_without_disturbing_others()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var keep = @"C:\Proj\Keep.feature";
        var remove = @"C:\Proj\Remove.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (keep, ProjectFileRole.Feature),
                (remove, ProjectFileRole.Feature)),
            CancellationToken.None);
        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (remove, ProjectFileRole.Feature, false)),
            CancellationToken.None);

        sut.IsPathOwned(keep).Should().BeTrue();
        sut.IsPathOwned(remove).Should().BeFalse();
    }

    // ── Deferred full re-scan (issue #48) ────────────────────────────────────

    [Fact]
    public async Task Baseline_before_the_project_is_live_sets_a_pending_full_rescan_and_does_not_publish()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj"); // deliberately NOT registered yet

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        _ = _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
        sut.TryConsumePendingFullRescan(project).Should().BeTrue();
    }

    [Fact]
    public void TryConsumePendingFullRescan_is_false_when_nothing_was_deferred()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");

        sut.TryConsumePendingFullRescan(project).Should().BeFalse();
    }

    [Fact]
    public async Task TryConsumePendingFullRescan_returns_true_only_once()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj"); // not registered — baseline races ahead

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.TryConsumePendingFullRescan(project).Should().BeTrue("first consumption sees the deferred flag");
        sut.TryConsumePendingFullRescan(project).Should().BeFalse("the flag was already consumed");
    }

    [Fact]
    public async Task Baseline_after_the_project_is_live_publishes_immediately_instead_of_deferring()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project); // live before the baseline arrives — the common case

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);

        sut.TryConsumePendingFullRescan(project).Should().BeFalse("nothing was deferred — the project was already live");
    }

    // ── Published notification shape ─────────────────────────────────────────

    [Fact]
    public async Task Baseline_publishes_a_full_replacement_notification_for_the_correct_project()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);
        await WaitForPublishAsync();

        _ = _mediator.Received(1).Publish(
            Arg.Is<BindingRegistryReplacedNotification>(n => n.Project == project),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delta_publishes_an_incremental_notification()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);
        await WaitForPublishAsync();
        _mediator.ClearReceivedCalls();

        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\B.feature", ProjectFileRole.Feature, true)),
            CancellationToken.None);
        await WaitForPublishAsync();

        _ = _mediator.Received(1).Publish(
            Arg.Is<BindingRegistryPatchedNotification>(n => n.Project == project),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delta_does_not_publish_when_the_project_is_not_currently_live()
    {
        // Edge case distinct from "no baseline yet": a baseline WAS received (so the delta is
        // not dropped), but the project has since become unresolvable via findProjectByKey.
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\A.feature", ProjectFileRole.Feature)),
            CancellationToken.None);
        await WaitForPublishAsync();
        _liveProjects.Clear(); // project no longer resolvable
        _mediator.ClearReceivedCalls();

        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (@"C:\Proj\B.feature", ProjectFileRole.Feature, true)),
            CancellationToken.None);

        // The delta itself still applies to the index...
        sut.IsPathOwned(@"C:\Proj\B.feature").Should().BeTrue();
        // ...but with no live project to attribute the notification to, nothing is published.
        _ = _mediator.DidNotReceiveWithAnyArgs().Publish(default!, default);
    }

    [Fact]
    public async Task Delta_removing_a_binding_file_reports_it_in_RemovedBindingFilePaths()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var stepsFile = @"C:\Proj\Steps.cs";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (stepsFile, ProjectFileRole.Binding)),
            CancellationToken.None);
        await WaitForPublishAsync();
        _mediator.ClearReceivedCalls();

        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (stepsFile, ProjectFileRole.Binding, false)),
            CancellationToken.None);
        // This delta publishes ProjectBindingFilesRemovedNotification then BindingRegistryPatchedNotification
        // in sequence (issue #577) -- wait for both before asserting on the first.
        await WaitForPublishAsync(atLeast: 2);

        _ = _mediator.Received(1).Publish(
            Arg.Is<ProjectBindingFilesRemovedNotification>(n =>
                n.Project == project && n.Paths.Contains(stepsFile)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delta_removing_a_feature_file_does_not_report_it_as_a_removed_binding_path()
    {
        var sut = CreateSut();
        var project = MakeProject(@"C:\Proj\My.csproj");
        Register(project);
        var featureFile = @"C:\Proj\A.feature";

        await sut.HandleProjectFilesAsync(
            BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (featureFile, ProjectFileRole.Feature)),
            CancellationToken.None);
        await WaitForPublishAsync();
        _mediator.ClearReceivedCalls();

        await sut.HandleProjectFilesAsync(
            DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                (featureFile, ProjectFileRole.Feature, false)),
            CancellationToken.None);
        await WaitForPublishAsync();

        // A feature-file (not binding-role) removal must not trigger the binding-files-removed
        // signal at all (issue #577 -- that notification is only published when there ARE
        // removed binding paths), though the delta's Patched notification still fires.
        _ = _mediator.DidNotReceive().Publish(
            Arg.Any<ProjectBindingFilesRemovedNotification>(), Arg.Any<CancellationToken>());
        _ = _mediator.Received(1).Publish(
            Arg.Is<BindingRegistryPatchedNotification>(n => n.Project == project),
            Arg.Any<CancellationToken>());
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_baselines_and_deltas_across_many_projects_are_applied_safely()
    {
        // _membership is a plain Dictionary guarded by _membershipLock (not a ConcurrentDictionary)
        // specifically because HandleProjectFilesAsync's baseline path needs multi-step
        // read-modify-write consistency; this drives real concurrent baseline + delta
        // notifications for many distinct projects to confirm the lock actually serializes access
        // correctly (no lost update, no corrupted _membership state) rather than just trusting the
        // single-threaded test coverage elsewhere in this file.
        var sut = CreateSut();
        const int projectCount = 25;
        var projects = Enumerable.Range(0, projectCount)
            .Select(i => MakeProject($@"C:\Proj{i}\My.csproj"))
            .ToArray();
        foreach (var project in projects)
            Register(project);

        await Task.WhenAll(projects.Select(async project =>
        {
            var featureFile = Path.Combine(Path.GetDirectoryName(project.ProjectFullName)!, "A.feature");
            var bindingFile = Path.Combine(Path.GetDirectoryName(project.ProjectFullName)!, "Steps.cs");

            await sut.HandleProjectFilesAsync(
                BaselineParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                    (featureFile, ProjectFileRole.Feature)),
                CancellationToken.None);

            await sut.HandleProjectFilesAsync(
                DeltaParams(project.ProjectFullName, project.TargetFrameworkMoniker,
                    (bindingFile, ProjectFileRole.Binding, true)),
                CancellationToken.None);
        }));

        foreach (var project in projects)
        {
            var featureFile = Path.Combine(Path.GetDirectoryName(project.ProjectFullName)!, "A.feature");
            var bindingFile = Path.Combine(Path.GetDirectoryName(project.ProjectFullName)!, "Steps.cs");

            sut.GetProjectsForUri(UriFor(featureFile)).Should().ContainSingle().Which.Should().BeSameAs(project);
            sut.IsPathOwned(bindingFile).Should().BeTrue();
            sut.HasBaselineForProject(project).Should().BeTrue();
        }
    }
}
