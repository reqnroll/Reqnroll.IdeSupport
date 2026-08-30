using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

public class LspWorkspaceScopeManagerTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly LspIdeScope _ideScope;
    private readonly LspWorkspaceScopeManager _sut;
    private readonly string _root1;
    private readonly string _root2;

    public LspWorkspaceScopeManagerTests()
    {
        _ideScope = new LspIdeScope(_logger);
        _sut = new LspWorkspaceScopeManager(_ideScope, _logger, _mediator);
        _root1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _root2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_root1);
        Directory.CreateDirectory(_root2);
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_root1)) Directory.Delete(_root1, recursive: true);
        if (Directory.Exists(_root2)) Directory.Delete(_root2, recursive: true);
    }

    // ── OpenWorkspace / GetScopeForUri ────────────────────────────────────────

    [Fact]
    public void GetScopeForUri_returns_null_when_no_workspaces_open()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        _sut.GetScopeForUri(uri).Should().BeNull();
    }

    [Fact]
    public void GetScopeForUri_returns_scope_after_OpenWorkspace()
    {
        _sut.OpenWorkspace(_root1);
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        _sut.GetScopeForUri(uri).Should().NotBeNull();
    }

    [Fact]
    public void GetScopeForUri_returns_null_for_uri_outside_all_workspaces()
    {
        _sut.OpenWorkspace(_root1);
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "a.feature");
        var uri = DocumentUri.FromFileSystemPath(outside);
        _sut.GetScopeForUri(uri).Should().BeNull();
    }

    [Fact]
    public void GetScopeForUri_returns_longest_prefix_scope_when_multiple_workspaces_open()
    {
        var nested = Path.Combine(_root1, "sub");
        Directory.CreateDirectory(nested);

        _sut.OpenWorkspace(_root1);
        _sut.OpenWorkspace(nested);

        var uri = DocumentUri.FromFileSystemPath(Path.Combine(nested, "a.feature"));
        var scope = _sut.GetScopeForUri(uri);
        scope.Should().NotBeNull();
        scope!.RootFolder.Should().Be(Path.GetFullPath(nested));
    }

    // ── CloseWorkspace ────────────────────────────────────────────────────────

    [Fact]
    public void CloseWorkspace_removes_scope_so_GetScopeForUri_returns_null()
    {
        _sut.OpenWorkspace(_root1);
        _sut.CloseWorkspace(_root1);

        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        _sut.GetScopeForUri(uri).Should().BeNull();
    }

    [Fact]
    public void CloseWorkspace_on_unknown_path_does_not_throw()
    {
        var act = () => _sut.CloseWorkspace(Path.Combine(Path.GetTempPath(), "never-opened-xyz"));
        act.Should().NotThrow();
    }

    // ── OpenWorkspace idempotency ─────────────────────────────────────────────

    [Fact]
    public void OpenWorkspace_called_twice_does_not_create_duplicate_scopes()
    {
        _sut.OpenWorkspace(_root1);
        _sut.OpenWorkspace(_root1); // second call should be a no-op

        var uriA = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        var uriB = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "b.feature"));

        // Both URIs should resolve to the same single scope instance.
        _sut.GetScopeForUri(uriA).Should().BeSameAs(_sut.GetScopeForUri(uriB));
    }

    // ── GetConfigurationProviderForUri ────────────────────────────────────────

    [Fact]
    public void GetConfigurationProviderForUri_returns_non_null_for_registered_workspace()
    {
        _sut.OpenWorkspace(_root1);
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        _sut.GetConfigurationProviderForUri(uri).Should().NotBeNull();
    }

    [Fact]
    public void GetConfigurationProviderForUri_returns_fallback_when_no_workspace_covers_uri()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        // No workspace opened — should fall back to ProjectSystemIdeSupportConfigurationProvider
        _sut.GetConfigurationProviderForUri(uri).Should().NotBeNull();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_removes_all_scopes()
    {
        _sut.OpenWorkspace(_root1);
        _sut.OpenWorkspace(_root2);
        _sut.Dispose();

        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root1, "a.feature"));
        _sut.GetScopeForUri(uri).Should().BeNull();
    }
}
