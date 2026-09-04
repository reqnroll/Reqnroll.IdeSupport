using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

/// <summary>
/// Direct unit tests for <see cref="CsFileReconciliationSelector"/> (issue #592), extracted from
/// <see cref="BindingRegistryChangedHandler"/> so the "which .cs files need Roslyn reconciliation
/// after a full replacement" selection policy — and in particular the build-staleness rule — is
/// testable without going through a full <see cref="BindingRegistryReplacedNotification"/>.
/// </summary>
public class CsFileReconciliationSelectorTests : IDisposable
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly ICSharpFileTextCache _csharpFileTextCache = new CSharpFileTextCache();
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IIdeSupportLogger _ideLogger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly string _projectFolder;

    public CsFileReconciliationSelectorTests()
    {
        _ideScope = new LspIdeScope(_ideLogger);
        _projectFolder = Path.Combine(Path.GetTempPath(), "CFRSTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectFolder)) Directory.Delete(_projectFolder, recursive: true); } catch (Exception ex) { Debug.WriteLine($"CsFileReconciliationSelectorTests: failed to clean up {_projectFolder}: {ex.Message}"); }
    }

    private CsFileReconciliationSelector CreateSut()
        => new(_scopeManager, _csharpFileTextCache, _fileSystem, _logger);

    /// <summary>Creates a project whose output assembly exists on disk with the given write time.</summary>
    private LspReqnrollProject MakeProjectWithBuiltAssembly(DateTime assemblyWriteUtc)
    {
        var assemblyPath = Path.Combine(_projectFolder, "bin", "Debug", "App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.WriteAllText(assemblyPath, "fake-dll");
        File.SetLastWriteTimeUtc(assemblyPath, assemblyWriteUtc);
        return DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder, outputAssemblyPath: assemblyPath);
    }

    /// <summary>Writes a .cs file under the project folder with a controlled last-write time.</summary>
    private string WriteCsFile(string name, string content, DateTime writeUtc)
    {
        var path = Path.Combine(_projectFolder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writeUtc);
        return path;
    }

    /// <summary>Marks the project as baselined and attributes the given .cs files to it in the index.</summary>
    private void IndexBindingFiles(LspReqnrollProject project, params string[] bindingFiles)
    {
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(project).Returns(bindingFiles);
        foreach (var path in bindingFiles)
        {
            var uri = DocumentUri.FromFileSystemPath(path);
            _scopeManager.ResolveOwners(uri).Returns(new[] { project });
        }
    }

    private static bool PathEq(string actual, string expected)
        => string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    // ── WasEditedSinceBuild (the staleness predicate) ──────────────────────────

    [Fact]
    public void WasEditedSinceBuild_is_true_when_the_file_is_newer_than_the_assembly()
    {
        var buildTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fileTime  = buildTime.AddSeconds(1);

        CsFileReconciliationSelector.WasEditedSinceBuild(fileTime, buildTime).Should().BeTrue();
    }

    [Fact]
    public void WasEditedSinceBuild_is_false_when_the_file_is_older_than_the_assembly()
    {
        var buildTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var fileTime  = buildTime.AddSeconds(-1);

        CsFileReconciliationSelector.WasEditedSinceBuild(fileTime, buildTime).Should().BeFalse();
    }

    [Fact]
    public void WasEditedSinceBuild_is_false_when_the_file_and_assembly_share_the_same_timestamp()
    {
        // The compiled DLL was written at exactly this instant (e.g. the build itself wrote the
        // source last) -- not "since" the build, so the compiled binding is still authoritative.
        var timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        CsFileReconciliationSelector.WasEditedSinceBuild(timestamp, timestamp).Should().BeFalse();
    }

    // ── Collect ─────────────────────────────────────────────────────────────

    [Fact]
    public void Collect_returns_empty_for_a_project_with_no_open_buffers_and_nothing_indexed()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);

        var result = CreateSut().Collect(project);

        result.Should().BeEmpty();
        project.Dispose();
    }

    [Fact]
    public void Collect_includes_an_open_project_owned_buffer_using_its_buffer_text()
    {
        var project = MakeProjectWithBuiltAssembly(DateTime.UtcNow.AddHours(-1));
        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow.AddHours(-2));
        var openUri = DocumentUri.FromFileSystemPath(openPath);
        _csharpFileTextCache.Update(openUri, "// unsaved buffer edit");
        IndexBindingFiles(project, openPath);

        var result = CreateSut().Collect(project);

        result.Should().ContainSingle(f => PathEq(f.FilePath, openPath) && f.Text == "// unsaved buffer edit");
        project.Dispose();
    }

    [Fact]
    public void Collect_excludes_an_open_buffer_owned_by_a_different_project()
    {
        var project = MakeProjectWithBuiltAssembly(DateTime.UtcNow.AddHours(-1));
        var otherProject = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder + "net481", outputAssemblyPath: null);

        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow.AddHours(-2));
        var openUri = DocumentUri.FromFileSystemPath(openPath);
        _csharpFileTextCache.Update(openUri, "// unsaved buffer edit");

        IndexBindingFiles(otherProject, openPath);
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetBindingFilePathsForProject(project).Returns(Array.Empty<string>());

        var result = CreateSut().Collect(project);

        result.Should().BeEmpty();
        project.Dispose();
        otherProject.Dispose();
    }

    [Fact]
    public void Collect_includes_a_closed_step_definition_file_edited_since_the_build()
    {
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project = MakeProjectWithBuiltAssembly(buildTime);
        var stepsPath = WriteCsFile("Steps.cs", "// renamed step", DateTime.UtcNow);
        IndexBindingFiles(project, stepsPath);

        var result = CreateSut().Collect(project);

        result.Should().ContainSingle(f => PathEq(f.FilePath, stepsPath) && f.Text.Contains("renamed step"));
        project.Dispose();
    }

    [Fact]
    public void Collect_excludes_a_closed_step_definition_file_unchanged_since_the_build()
    {
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project = MakeProjectWithBuiltAssembly(buildTime);
        var stepsPath = WriteCsFile("Steps.cs", "// in sync with DLL", DateTime.UtcNow.AddHours(-2));
        IndexBindingFiles(project, stepsPath);

        var result = CreateSut().Collect(project);

        result.Should().BeEmpty();
        project.Dispose();
    }

    [Fact]
    public void Collect_ignores_closed_files_entirely_when_the_project_has_no_built_assembly()
    {
        // No output assembly exists -- MakeProject's default OutputAssemblyPath points at a file
        // that was never created -- so nothing compiled can be stale, and closed files aren't read.
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _projectFolder);
        var stepsPath = WriteCsFile("Steps.cs", "// newer than nothing", DateTime.UtcNow);
        IndexBindingFiles(project, stepsPath);

        var result = CreateSut().Collect(project);

        result.Should().BeEmpty();
        project.Dispose();
    }

    [Fact]
    public void Collect_does_not_duplicate_a_closed_file_that_is_also_an_open_buffer()
    {
        var buildTime = DateTime.UtcNow.AddHours(-1);
        var project = MakeProjectWithBuiltAssembly(buildTime);
        var openPath = WriteCsFile("OpenSteps.cs", "// stale disk text", DateTime.UtcNow);
        var openUri = DocumentUri.FromFileSystemPath(openPath);
        _csharpFileTextCache.Update(openUri, "// unsaved buffer edit");
        IndexBindingFiles(project, openPath);

        var result = CreateSut().Collect(project);

        result.Should().ContainSingle();
        result[0].Text.Should().Be("// unsaved buffer edit");
        project.Dispose();
    }
}
