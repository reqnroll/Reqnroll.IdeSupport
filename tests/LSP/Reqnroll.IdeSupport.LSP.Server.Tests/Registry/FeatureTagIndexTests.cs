using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Registry;

/// <summary>
/// Tests for <see cref="FeatureTagIndex"/> against a real file system and the real
/// <see cref="IdeSupportTagParser"/> — open buffers preferred over disk, closed files parsed from
/// disk once and cached until their write time changes, and the membership-index file list
/// overriding the disk scan once the project baseline has arrived (issue #828).
/// </summary>
public class FeatureTagIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ReqnrollTagIndexTests", Guid.NewGuid().ToString("N"));

    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();

    public FeatureTagIndexTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private FeatureTagIndex CreateProvider()
    {
        var telemetry = Substitute.For<IErrorTelemetryService>();
        var config = Substitute.For<IIdeSupportConfigurationProvider>();
        config.GetConfiguration().Returns(new IdeSupportConfiguration());

        var tagParser = new IdeSupportTagParser(_logger, telemetry, config);
        return new FeatureTagIndex(
            _scopeManager, _bufferService, tagParser, new FileSystemForIDE(), _logger, _telemetry);
    }

    /// <summary>Registers a real project rooted at <paramref name="folder"/> as the sole owner of <paramref name="uri"/>.</summary>
    private LspReqnrollProject RegisterOwner(DocumentUri uri, string folder, bool hasBaseline = false,
        IReadOnlyCollection<string>? indexedFiles = null)
    {
        var ideScope = Substitute.For<IIdeScope>();
        ideScope.FileSystem.Returns(new FileSystemForIDE());

        var project = new LspReqnrollProject(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder = folder,
            ProjectFile = Path.Combine(folder, "Sample.csproj"),
            ProjectFolder = folder,
            OutputAssemblyPath = Path.Combine(folder, "bin", "Debug", "Sample.dll"),
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        }, ideScope);

        _scopeManager.ResolveOwners(uri).Returns(new[] { project });
        _scopeManager.HasBaselineForProject(project).Returns(hasBaseline);
        if (indexedFiles is not null)
            _scopeManager.GetIndexedFeatureFiles(project).Returns(indexedFiles.ToArray());

        return project;
    }

    /// <summary>
    /// Registers an open buffer. <paramref name="parsed"/> mirrors the background parse having
    /// landed (<c>buffer.Tags</c> populated); <c>false</c> mirrors the window right after an edit,
    /// where <c>DocumentBufferService.Update</c> has reset <c>Tags</c> to null.
    /// </summary>
    private void SetupBuffer(DocumentUri uri, int version, string text, bool parsed = true)
    {
        IReadOnlyCollection<IdeSupportTag>? tags = null;
        if (parsed)
        {
            var config = Substitute.For<IIdeSupportConfigurationProvider>();
            config.GetConfiguration().Returns(new IdeSupportConfiguration());
            var parser = new IdeSupportTagParser(_logger, Substitute.For<IErrorTelemetryService>(), config);
            tags = parser.Parse(new LspTextSnapshot(string.Empty, 0, text), ProjectBindingRegistry.Invalid);
        }

        var buffer = new DocumentBuffer(uri, version, text, tags);
        DocumentBuffer? outBuffer;
        _bufferService.TryGet(uri, out outBuffer).Returns(x => { x[1] = buffer; return true; });
    }

    private static string WriteFeature(string folder, string fileName, string content)
    {
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string FeatureWithTags(string tags)
        => $"{tags}\nFeature: F\nScenario: S\n    Given a step\n";

    // ── Disk indexing (pre-baseline fallback) ────────────────────────────────

    [Fact]
    public async Task Closed_feature_files_are_indexed_from_disk()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "A.feature", FeatureWithTags("@smoke @wip"));
        WriteFeature(_root, "B.feature", FeatureWithTags("@smoke"));

        var provider = CreateProvider();
        var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        result.Select(t => t.Sample).Should().BeEquivalentTo("@smoke", "@wip");
        result.Single(t => t.Sample == "@smoke").UsageCount.Should().Be(2, "used once in each of the two sibling files");
        result.Single(t => t.Sample == "@wip").UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task Files_outside_the_project_folder_are_indexed_once_the_baseline_has_arrived()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        var linkedFile = Path.Combine(_root, "..", $"Linked.{Guid.NewGuid():N}.feature");
        try
        {
            File.WriteAllText(linkedFile, FeatureWithTags("@linked"));

            RegisterOwner(uri, _root, hasBaseline: true,
                indexedFiles: new[] { Path.Combine(_root, "Target.feature"), linkedFile });

            // A file on disk under the project folder that the baseline excludes must not leak in.
            WriteFeature(_root, "Excluded.feature", FeatureWithTags("@excluded"));

            var provider = CreateProvider();
            var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

            result.Select(t => t.Sample).Should().Contain("@linked");
            result.Select(t => t.Sample).Should().NotContain("@excluded");
        }
        finally
        {
            if (File.Exists(linkedFile)) File.Delete(linkedFile);
        }
    }

    [Fact]
    public async Task An_open_buffer_wins_over_the_disk_content()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "Target.feature", FeatureWithTags("@smoke"));
        SetupBuffer(uri, version: 3, text: FeatureWithTags("@wip"));

        var provider = CreateProvider();
        var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        result.Select(t => t.Sample).Should().Contain("@wip")
            .And.NotContain("@smoke", "the unsaved buffer content is the source of truth for an open document");
    }

    // ── Caching ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closed_files_are_cached_until_their_write_time_changes()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        var path = WriteFeature(_root, "Target.feature", FeatureWithTags("@smoke"));
        var originalWriteTime = File.GetLastWriteTimeUtc(path);

        var provider = CreateProvider();
        var first = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        first.Select(t => t.Sample).Should().Contain("@smoke");

        // Content changed but the write time is unchanged (e.g. a fast successive write within
        // the filesystem's timestamp granularity): the cache must not notice, so the stale
        // result is served without a re-read.
        File.WriteAllText(path, FeatureWithTags("@wip"));
        File.SetLastWriteTimeUtc(path, originalWriteTime);

        var second = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        second.Select(t => t.Sample).Should().Contain("@smoke", "cached while the write time is unchanged");

        // Bump the write time: the next request must re-parse and see the new tags.
        File.SetLastWriteTimeUtc(path, originalWriteTime.AddSeconds(1));
        if (File.GetLastWriteTimeUtc(path) == originalWriteTime)
        {
            // Extremely coarse filesystem timestamps: step far enough to force a change.
            File.SetLastWriteTimeUtc(path, originalWriteTime.AddHours(1));
        }

        var third = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        third.Select(t => t.Sample).Should().Contain("@wip", "re-parsed once the write time changes");
    }

    [Fact]
    public async Task An_open_buffer_is_cached_by_version_only()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "Target.feature", FeatureWithTags("@smoke"));
        SetupBuffer(uri, version: 1, text: FeatureWithTags("@wip"));

        var provider = CreateProvider();
        var first = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        first.Select(t => t.Sample).Should().Contain("@wip");

        // Same version, same buffer content — served from the version-keyed cache.
        var second = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        second.Select(t => t.Sample).Should().Contain("@wip");

        // A newer version with different content must be re-extracted.
        SetupBuffer(uri, version: 2, text: FeatureWithTags("@slow"));
        var third = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        third.Select(t => t.Sample).Should().BeEquivalentTo("@slow");
    }

    [Fact]
    public async Task While_the_background_parse_is_pending_the_last_known_counts_are_reused_without_reparsing()
    {
        // An edit resets buffer.Tags to null until the scheduled parse lands. Completion must not
        // add a parse of its own: it keeps serving what it last knew (best-effort accuracy).
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "Target.feature", FeatureWithTags("@disk"));
        SetupBuffer(uri, version: 1, text: FeatureWithTags("@wip"));

        var provider = CreateProvider();
        (await provider.GetTagCandidatesAsync(uri, CancellationToken.None))
            .Select(t => t.Sample).Should().BeEquivalentTo(new[] { "@wip" });

        SetupBuffer(uri, version: 2, text: FeatureWithTags("@slow"), parsed: false);
        var pending = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        pending.Select(t => t.Sample).Should().BeEquivalentTo(new[] { "@wip" },
            "the unparsed edit is ignored for now rather than reparsed on the completion path");

        SetupBuffer(uri, version: 2, text: FeatureWithTags("@slow"));
        var landed = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        landed.Select(t => t.Sample).Should().BeEquivalentTo(new[] { "@slow" }, "the parse landed, so the new version is counted");
    }

    [Fact]
    public async Task An_open_buffer_that_has_never_been_parsed_falls_back_to_the_disk_content()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "Target.feature", FeatureWithTags("@smoke"));
        SetupBuffer(uri, version: 1, text: FeatureWithTags("@wip"), parsed: false);

        var provider = CreateProvider();
        var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        result.Select(t => t.Sample).Should().BeEquivalentTo(new[] { "@smoke" },
            "with no parsed tags and nothing cached, the saved file is the best available source");
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_scan_of_a_project_sends_one_counts_only_telemetry_event()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "A.feature", FeatureWithTags("@smoke @wip"));
        WriteFeature(_root, "B.feature", FeatureWithTags("@smoke"));

        var provider = CreateProvider();
        await provider.GetTagCandidatesAsync(uri, CancellationToken.None);
        await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        _telemetry.Received(1).SendEvent(TelemetryEvents.TagIndexFirstScanCompleted, Arg.Is<Dictionary<string, object?>>(p =>
            (int)p["FileCount"]! == 2 &&
            (int)p["FilesParsedFromDisk"]! == 2 &&
            (int)p["DistinctTagCount"]! == 2 &&
            (long)p["DurationMs"]! >= 0 &&
            p.Count == 4));
    }

    [Fact]
    public async Task Telemetry_never_carries_project_file_or_tag_names()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        RegisterOwner(uri, _root, hasBaseline: false);
        WriteFeature(_root, "Secret.feature", FeatureWithTags("@confidential"));

        await CreateProvider().GetTagCandidatesAsync(uri, CancellationToken.None);

        _telemetry.Received(1).SendEvent(TelemetryEvents.TagIndexFirstScanCompleted, Arg.Is<Dictionary<string, object?>>(p =>
            p.Values.All(v => v is int || v is long)));
    }

    // ── Ownership / aggregation ──────────────────────────────────────────────

    [Fact]
    public async Task Unowned_documents_yield_no_candidates()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Target.feature"));
        _scopeManager.ResolveOwners(uri).Returns(Array.Empty<LspReqnrollProject>());

        var provider = CreateProvider();
        var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Tags_from_multiple_owning_projects_are_merged()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "Shared.feature"));
        var folderA = Path.Combine(_root, "A");
        var folderB = Path.Combine(_root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);

        var ideScope = Substitute.For<IIdeScope>();
        ideScope.FileSystem.Returns(new FileSystemForIDE());
        var projectA = new LspReqnrollProject(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder = _root,
            ProjectFile = Path.Combine(folderA, "A.csproj"),
            ProjectFolder = folderA,
            OutputAssemblyPath = Path.Combine(folderA, "bin", "Debug", "A.dll"),
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        }, ideScope);
        var projectB = new LspReqnrollProject(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder = _root,
            ProjectFile = Path.Combine(folderB, "B.csproj"),
            ProjectFolder = folderB,
            OutputAssemblyPath = Path.Combine(folderB, "bin", "Debug", "B.dll"),
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
        }, ideScope);

        _scopeManager.ResolveOwners(uri).Returns(new[] { projectA, projectB });

        WriteFeature(folderA, "A.feature", FeatureWithTags("@smoke"));
        WriteFeature(folderB, "B.feature", FeatureWithTags("@smoke @wip"));

        var provider = CreateProvider();
        var result = await provider.GetTagCandidatesAsync(uri, CancellationToken.None);

        result.Select(t => t.Sample).Should().BeEquivalentTo("@smoke", "@wip");
        result.Single(t => t.Sample == "@smoke").UsageCount.Should().Be(2, "one occurrence in each owning project");
    }
}
