using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>Tests for <see cref="CSharpDiagnosticsRegistryChangedHandler"/>.</summary>
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

    [Fact]
    public async Task Publishes_for_every_open_cs_file_owned_by_the_project()
    {
        var uri1 = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        var uri2 = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "B.cs"));
        _csharpFileTextCache.Update(uri1, "// a");
        _csharpFileTextCache.Update(uri2, "// b");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(Arg.Any<DocumentUri>()).Returns([_project]);

        await CreateSut().Handle(new BindingRegistryChangedNotification(_project), CancellationToken.None);

        _publisher.Received(1).Publish(uri1, null);
        _publisher.Received(1).Publish(uri2, null);
    }

    [Fact]
    public async Task Does_not_publish_for_a_file_owned_by_a_different_project()
    {
        var ownUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "Owned.cs"));
        var otherUri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "NotOwned.cs"));
        _csharpFileTextCache.Update(ownUri, "// owned");
        _csharpFileTextCache.Update(otherUri, "// not owned");

        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(ownUri).Returns([_project]);
        _scopeManager.GetProjectsForUri(otherUri).Returns([_otherProject]);

        await CreateSut().Handle(new BindingRegistryChangedNotification(_project), CancellationToken.None);

        _publisher.Received(1).Publish(ownUri, null);
        _publisher.DidNotReceive().Publish(otherUri, null);
    }

    [Fact]
    public async Task Falls_back_to_folder_prefix_when_no_baseline_exists()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        _csharpFileTextCache.Update(uri, "// a");

        _scopeManager.HasBaselineForProject(_project).Returns(false);

        await CreateSut().Handle(new BindingRegistryChangedNotification(_project), CancellationToken.None);

        _publisher.Received(1).Publish(uri, null);
    }

    [Fact]
    public async Task Handling_the_same_notification_twice_republishes_each_file_unchanged()
    {
        // BindingRegistryChangedNotification consumers must be idempotent (issue #578). This
        // handler's own remarks describe it as re-pushing "every open .cs file owned by the
        // project unconditionally on any change (not diffed first)" — so a duplicate publish
        // (the connector's startup reconciliation racing this file's own didOpen, per the
        // handler's documented rationale) must republish the same files the same way each time,
        // not skip the second call or diverge from the first.
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_projectFolder, "A.cs"));
        _csharpFileTextCache.Update(uri, "// a");
        _scopeManager.HasBaselineForProject(_project).Returns(true);
        _scopeManager.GetProjectsForUri(uri).Returns([_project]);

        var sut = CreateSut();
        var notification = new BindingRegistryChangedNotification(_project);
        await sut.Handle(notification, CancellationToken.None);
        await sut.Handle(notification, CancellationToken.None);

        _publisher.Received(2).Publish(uri, null);
    }
}
