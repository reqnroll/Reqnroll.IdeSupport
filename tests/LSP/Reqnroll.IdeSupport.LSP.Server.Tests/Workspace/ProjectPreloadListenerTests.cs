using System.IO.Pipes;
using System.Text;
using MediatR;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Workspace;

/// <summary>
/// <see cref="ProjectPreloadListener"/> is the server-side end of the eager-startup preload
/// channel (see docs/architecture/client-visualstudio.md, eager start and preload): it must dispatch
/// <c>reqnroll/projectLoaded</c> / <c>reqnroll/projectFiles</c> payloads arriving on its named
/// pipe to <see cref="ILspWorkspaceScopeManager"/> exactly as the real LSP notification handlers
/// would (see <see cref="LspWorkspaceScopeManagerTests"/> for the handler-level coverage) — this
/// class only needs to prove the pipe-framing/dispatch plumbing itself.
/// </summary>
public class ProjectPreloadListenerTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly LspWorkspaceScopeManager _scopeManager;
    private readonly string _root;

    public ProjectPreloadListenerTests()
    {
        _ideScope     = new LspIdeScope(_logger);
        _scopeManager = new LspWorkspaceScopeManager(_ideScope, _logger, Substitute.For<IMediator>());
        _root         = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _scopeManager.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Dispatches_a_projectLoaded_message_to_the_scope_manager()
    {
        var pipeName    = "reqnroll-preload-test-" + Guid.NewGuid().ToString("N");
        using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listenTask  = ProjectPreloadListener.RunAsync(_scopeManager, _logger, cts.Token, pipeName);

        var projectFile = Path.Combine(_root, "Project.csproj");
        await WriteEnvelopeAsync(pipeName, "reqnroll/projectLoaded", $$"""
            {"workspaceFolder":"{{EscapeJson(_root)}}","projectFile":"{{EscapeJson(projectFile)}}",
             "projectFolder":"{{EscapeJson(_root)}}","outputAssemblyPath":"",
             "targetFrameworkMoniker":"","packageReferences":[]}
            """.ReplaceLineEndings(""), cts.Token);

        var uri = DocumentUri.FromFileSystemPath(projectFile);
        await WaitUntilAsync(() => _scopeManager.GetScopeForUri(uri) is not null, cts.Token);

        _scopeManager.GetScopeForUri(uri).Should().NotBeNull();
        _scopeManager.GetProjectForUri(uri)?.ProjectFullName.Should().Be(projectFile);

        await cts.CancelAsync();
        await listenTask;
    }

    [Fact]
    public async Task Dispatches_a_projectFiles_baseline_to_the_membership_index()
    {
        var pipeName   = "reqnroll-preload-test-" + Guid.NewGuid().ToString("N");
        using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listenTask = ProjectPreloadListener.RunAsync(_scopeManager, _logger, cts.Token, pipeName);

        var projectFile = Path.Combine(_root, "Project.csproj");
        var featureFile = Path.Combine(_root, "a.feature");

        // A projectLoaded baseline must precede projectFiles for the project to be resolvable
        // by the membership index — same ordering the real IDE glue uses.
        await WriteEnvelopeAsync(pipeName, "reqnroll/projectLoaded", $$"""
            {"workspaceFolder":"{{EscapeJson(_root)}}","projectFile":"{{EscapeJson(projectFile)}}",
             "projectFolder":"{{EscapeJson(_root)}}","outputAssemblyPath":"",
             "targetFrameworkMoniker":"","packageReferences":[]}
            """.ReplaceLineEndings(""), cts.Token);
        var uri = DocumentUri.FromFileSystemPath(projectFile);
        await WaitUntilAsync(() => _scopeManager.GetScopeForUri(uri) is not null, cts.Token);

        await WriteEnvelopeAsync(pipeName, "reqnroll/projectFiles", $$"""
            {"projectFile":"{{EscapeJson(projectFile)}}","targetFrameworkMoniker":"","kind":0,
             "files":[{"path":"{{EscapeJson(featureFile)}}","role":0,"added":true}]}
            """.ReplaceLineEndings(""), cts.Token);

        var project    = _scopeManager.GetProjectForUri(uri)!;
        await WaitUntilAsync(() => _scopeManager.HasBaselineForProject(project), cts.Token);

        _scopeManager.GetIndexedFeatureFiles(project).Should().ContainSingle()
            .Which.Should().Be(featureFile);

        await cts.CancelAsync();
        await listenTask;
    }

    private static async Task WriteEnvelopeAsync(
        string pipeName, string method, string paramsJson, CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        var line  = $"{{\"method\":\"{method}\",\"params\":{paramsJson}}}\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await client.WriteAsync(bytes, 0, bytes.Length, ct);
        await client.FlushAsync(ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition() && !ct.IsCancellationRequested)
            await Task.Delay(25, ct);
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\");
}
