using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Tests for <see cref="CSharpDiagnosticsRegistryChangedHandler"/>. Since issue #577 split the
/// single <c>BindingRegistryChangedNotification</c> it used to handle into three named events,
/// the ownership-filtering tests below run once per event type (via <see cref="AllThreeEvents"/>)
/// to confirm dispatch is wired correctly for all three, not just whichever one happened to be
/// picked as a representative.
/// </summary>
public class CSharpDiagnosticsRegistryChangedHandlerTests : IDisposable
{
    private readonly ICSharpFileTextCache _csharpFileTextCache = new CSharpFileTextCache();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly ICSharpDiagnosticsPublisher _publisher = Substitute.For<ICSharpDiagnosticsPublisher>();

    private readonly IIdeSupportLogger _ideLogger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly string _projectFolder;
    private readonly LspReqnrollProject _project;
    private readonly LspReqnrollProject _otherProject;

    public CSharpDiagnosticsRegistryChangedHandlerTests()
    {
        _ideScope = new LspIdeScope(_ideLogger);
        _projectFolder = Path.Combine(Path.GetTempPath(), "CSDRCHTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);

        _project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);
        _otherProject = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);
    }

    public void Dispose()
    {
        _project.Dispose();
        _otherProject.Dispose();
        try { if (Directory.Exists(_projectFolder)) Directory.Delete(_projectFolder, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private CSharpDiagnosticsRegistryChangedHandler CreateSut() =>
        new(_csharpFileTextCache, _scopeManager, _publisher);

    /// <summary>The three events this handler now subscribes to (issue #577), each carrying <see cref="_project"/>.</summary>
    public static IEnumerable<object[]> AllThreeEvents()
    {
        yield return new object[] { "Replaced" };
        yield return new object[] { "Patched" };
        yield return new object[] { "Removed" };
    }

    /// <summary>Dispatches to the correctly-typed <c>Handle</c> overload for <paramref name="eventName"/>, so the [Theory] tests below can share one call site across the three distinct method signatures.</summary>
    private Task InvokeHandleAsync(CSharpDiagnosticsRegistryChangedHandler sut, string eventName, LspReqnrollProject project, CancellationToken ct)
        => eventName switch
        {
            "Replaced" => sut.Handle(new BindingRegistryReplacedNotification(project), ct),
            "Patched" => sut.Handle(new BindingRegistryPatchedNotification(project), ct),
            "Removed" => sut.Handle(new ProjectBindingFilesRemovedNotification(project, Array.Empty<string>()), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null)
        };

    [Theory]
    [MemberData(nameof(AllThreeEvents))]
    public async Task Publishes_for_every_open_cs_file_owned_by_the_project(string eventName)
    {
        var uri1 = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        var uri2 = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "B.cs"));
        _csharpFileTextCache.Update(uri1, "// a");
        _csharpFileTextCache.Update(uri2, "// b");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(Arg.Any<DocumentUri>()).Returns([_project]);

        await InvokeHandleAsync(CreateSut(), eventName, _project, CancellationToken.None);

        _publisher.Received(1).Publish(uri1, null);
        _publisher.Received(1).Publish(uri2, null);
    }

    [Theory]
    [MemberData(nameof(AllThreeEvents))]
    public async Task Does_not_publish_for_a_file_owned_by_a_different_project(string eventName)
    {
        var ownUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Owned.cs"));
        var otherUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "NotOwned.cs"));
        _csharpFileTextCache.Update(ownUri, "// owned");
        _csharpFileTextCache.Update(otherUri, "// not owned");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(ownUri).Returns([_project]);
        _scopeManager.GetProjectsForUri(otherUri).Returns([_otherProject]);

        await InvokeHandleAsync(CreateSut(), eventName, _project, CancellationToken.None);

        _publisher.Received(1).Publish(ownUri, null);
        _publisher.DidNotReceive().Publish(otherUri, null);
    }

    [Theory]
    [MemberData(nameof(AllThreeEvents))]
    public async Task Falls_back_to_folder_prefix_when_no_baseline_exists(string eventName)
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        _csharpFileTextCache.Update(uri, "// a");

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await InvokeHandleAsync(CreateSut(), eventName, _project, CancellationToken.None);

        _publisher.Received(1).Publish(uri, null);
    }

    [Fact]
    public async Task Handling_the_same_notification_twice_republishes_each_file_unchanged()
    {
        // BindingRegistryReplacedNotification (and its two siblings) must be idempotent: handling
        // the same notification twice must leave the same observable end state as handling it
        // once. This handler's own remarks describe it as re-pushing "every open .cs file owned
        // by the project unconditionally on any change (not diffed first)", so a duplicate
        // publish must republish the same files the same way each time, not skip the second call.
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        _csharpFileTextCache.Update(uri, "// a");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(uri).Returns([_project]);

        var sut = CreateSut();
        var notification = new BindingRegistryReplacedNotification(_project);
        await sut.Handle(notification, CancellationToken.None);
        await sut.Handle(notification, CancellationToken.None);

        _publisher.Received(2).Publish(uri, null);
    }
}
