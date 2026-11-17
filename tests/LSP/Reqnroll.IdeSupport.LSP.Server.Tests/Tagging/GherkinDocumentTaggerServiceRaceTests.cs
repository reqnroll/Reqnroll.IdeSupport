using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

/// <summary>
/// Characterises orderings in which a derivation built from older inputs (text, registry or project
/// configuration) is stored after one built from newer inputs. Each test drives one interleaving
/// deterministically: <see cref="GherkinDocumentTaggerService"/> parses synchronously, so a hook in the
/// tag parser or the buffer service runs the competing writer at an exact point, with no threads or
/// timing. Every test asserts the desired outcome — the newer derivation stays in the cache — against
/// the real <see cref="BindingMatchService"/> and <see cref="DocumentBufferService"/>.
/// <para>
/// The hooks replay what the handlers do (didOpen, didClose, a rename, a configuration change) by
/// calling the services directly. Whatever those handlers do to record that an input changed must be
/// replayed here the same way, or a test stops modelling the handler it stands for.
/// </para>
/// </summary>
public sealed class GherkinDocumentTaggerServiceRaceTests : IDisposable
{
    private const string NotYetEnforced =
        "Stale derived-state write is accepted until the versioned-derived-state store guard is enforced";

    private const string OneScenario =
        "Feature: F\n\nScenario: One\n    Given a step\n";

    private const string TwoScenarios =
        "Feature: F\n\nScenario: One\n    Given a step\n\nScenario: Two\n    Given another step\n";

    private const string FrenchFeature =
        "Fonctionnalité: Une fonctionnalité\n\nScénario: Un scénario\n    Etant donné une précondition\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rnr-race-" + Guid.NewGuid().ToString("N"));

    private readonly IIdeSupportLogger             _logger         = Substitute.For<IIdeSupportLogger>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly BindingMatchService           _matchService   = new();
    private readonly HookedBufferService           _bufferService  = new(new DocumentBufferService());
    private readonly HookedTagParser               _tagParser;

    private readonly LspReqnrollProject _project;
    private readonly ProjectOwner       _owner;
    private readonly DocumentUri        _uri;
    private readonly string             _featurePath;

    public GherkinDocumentTaggerServiceRaceTests()
    {
        Directory.CreateDirectory(_root);
        _tagParser = new HookedTagParser(TaggerTestParser.Create(_logger));
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);

        _project = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = _root,
                ProjectFile            = Path.Combine(_root, "My.csproj"),
                ProjectFolder          = _root,
                OutputAssemblyPath     = Path.Combine(_root, "bin", "My.dll"),
                TargetFrameworkMoniker = "net10.0"
            },
            new LspIdeScope(_logger));
        _owner = new ProjectOwner(_project.ProjectFullName, _project.TargetFrameworkMoniker);

        _featurePath = Path.Combine(_root, "race.feature");
        _uri         = DocumentUri.FromFileSystemPath(_featurePath);
        _scopeManager.ResolvePrimaryOwner(_uri).Returns(_project);
        _scopeManager.ResolveOwners(_uri).Returns(new[] { _project });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GherkinDocumentTaggerService CreateSut() =>
        new(_bufferService, _tagParser, _registryLookup, Substitute.For<ISemanticTokensService>(),
            _matchService, _scopeManager, _logger, new FileSystemForIDE(),
            new DocumentGenerations(), new ProjectConfigGenerations());

    private FeatureBindingMatchSet Cached()
    {
        _matchService.TryGet(new MatchSetKey(_uri.ToString(), _owner), out var set)
            .Should().BeTrue("the document must still have a match set for its owner");
        return set;
    }

    // R1: ScanClosedFileAsync's "is it open?" guard is check-then-act. A didOpen and its parse can land
    // after the guard and before the closed scan's Store.
    [Fact(Skip = NotYetEnforced)]
    public async Task Closed_scan_storing_after_the_file_was_opened_and_parsed_keeps_the_open_documents_match_set()
    {
        var sut = CreateSut();
        _tagParser.OnNextParse = snapshot =>
        {
            snapshot.Version.Should().Be(0, "the hook must fire inside the closed-file scan");
            _bufferService.Update(_uri, 5, OneScenario); // didOpen
            sut.ParseAsync(_uri, 5).GetAwaiter().GetResult(); // its parse completes first
        };

        await sut.ScanClosedFileAsync(_uri, OneScenario, _project, 0);

        Cached().DocumentVersion.Should().Be(5, "the open document's parse is newer than the closed-file scan");
    }

    // R2: a parse whose UpdateTags succeeded before didClose stores after the close has invalidated and
    // rescanned the file from disk.
    [Fact(Skip = NotYetEnforced)]
    public async Task Parse_storing_after_close_and_rescan_keeps_the_disk_based_match_set()
    {
        await File.WriteAllTextAsync(_featurePath, OneScenario);
        var sut = CreateSut();
        _bufferService.Update(_uri, 3, TwoScenarios); // unsaved edits, discarded on close
        _bufferService.AfterNextUpdateTags = () =>
        {
            // What TextDocumentSyncHandler's didClose does, landing between UpdateTags and Store.
            _bufferService.Remove(_uri);
            _matchService.InvalidateAllForDocument(_uri.ToString());
            sut.RescanClosedFileAsync(_uri).GetAwaiter().GetResult();
        };

        await sut.ParseAsync(_uri, 3);

        var cached = Cached();
        cached.DocumentVersion.Should().BeNull("after didClose the file on disk is the source of truth");
        cached.Scenarios.Should().HaveCount(1, "the discarded editor text must not come back");
    }

    // R4a: didChange replaces the buffer inline while the previous version's parse is still running; that
    // parse then attaches its tags to the newer buffer.
    [Fact(Skip = "Older tags are attached to a newer buffer until UpdateTags checks the content generation")]
    public async Task Parse_of_an_older_buffer_does_not_attach_its_tags_to_the_newer_buffer()
    {
        var sut = CreateSut();
        _bufferService.Update(_uri, 1, OneScenario);
        _tagParser.OnNextParse = snapshot =>
        {
            snapshot.Version.Should().Be(1);
            _bufferService.Update(_uri, 2, TwoScenarios); // didChange lands after the version check
        };

        var olderTags = await sut.ParseAsync(_uri, 1);

        _bufferService.TryGet(_uri, out var buffer).Should().BeTrue();
        buffer!.Version.Should().Be(2);
        buffer.Tags.Should().NotBeSameAs(olderTags, "version 1's tags do not describe version 2's text");
    }

    // R5 (late write): a closed scan that read the file before a rename edited it stores after the rename
    // has invalidated the file and its post-rename rescan has stored.
    [Fact(Skip = NotYetEnforced)]
    public async Task Closed_scan_of_pre_rename_text_storing_after_the_post_rename_rescan_keeps_the_new_text()
    {
        await File.WriteAllTextAsync(_featurePath, OneScenario);
        var sut = CreateSut();
        _tagParser.OnNextParse = _ =>
        {
            // The rename lands mid-scan: new text on disk, invalidation, re-derivation from disk.
            File.WriteAllText(_featurePath, TwoScenarios);
            _matchService.InvalidateAllForDocument(_uri.ToString());
            sut.RescanClosedFileAsync(_uri).GetAwaiter().GetResult();
        };

        await sut.ScanClosedFileAsync(_uri, OneScenario, _project, 0);

        Cached().Scenarios.Should().HaveCount(2, "the scan of the pre-rename text is older than the rescan");
    }

    // R6: a reqnroll.json change re-derives the project's files with the same text and the same registry.
    // A scan that read the old feature language can store after one that read the new one.
    [Fact(Skip = NotYetEnforced)]
    public async Task Closed_scan_with_the_old_feature_language_storing_last_keeps_the_new_language_result()
    {
        var configuration = new IdeSupportConfiguration(); // English
        var configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
        configProvider.GetConfiguration().Returns(_ => configuration);
        _project.Properties[typeof(IIdeSupportConfigurationProvider)] = configProvider;
        _project.GetIdeSupportConfiguration().DefaultFeatureLanguage
            .Should().Be(IdeSupportConfiguration.FallbackFeatureLanguage);

        var sut = CreateSut();
        _tagParser.OnNextParse = _ =>
        {
            // reqnroll.json switches the project to French while the English scan is still parsing,
            // and the reconcile's scan with the new language completes first.
            configuration = new IdeSupportConfiguration { DefaultFeatureLanguage = "fr" };
            sut.ScanClosedFileAsync(_uri, FrenchFeature, _project, 0).GetAwaiter().GetResult();
        };

        await sut.ScanClosedFileAsync(_uri, FrenchFeature, _project, 0);

        Cached().Scenarios.Should().HaveCount(1, "only the scan with the new (French) language understands the file");
    }

    /// <summary>Tag parser that runs a one-shot hook before delegating, so a competing writer can run mid-parse.</summary>
    private sealed class HookedTagParser(IIdeSupportTagParser inner) : IIdeSupportTagParser
    {
        public Action<IGherkinTextSnapshot>? OnNextParse { get; set; }

        public IReadOnlyCollection<IdeSupportTag> Parse(
            IGherkinTextSnapshot fileSnapshot, ProjectBindingRegistry bindingRegistry, string defaultFeatureLanguage)
        {
            var hook = OnNextParse;
            OnNextParse = null;
            hook?.Invoke(fileSnapshot);
            return inner.Parse(fileSnapshot, bindingRegistry, defaultFeatureLanguage);
        }
    }

    /// <summary>Buffer service that runs a one-shot hook after a successful <c>UpdateTags</c>.</summary>
    private sealed class HookedBufferService(IDocumentBufferService inner) : IDocumentBufferService
    {
        public Action? AfterNextUpdateTags { get; set; }

        public void Update(DocumentUri uri, int? version, string text, long generation = 0) => inner.Update(uri, version, text, generation);

        public bool UpdateTags(DocumentUri uri, IReadOnlyCollection<IdeSupportTag> tags)
        {
            var updated = inner.UpdateTags(uri, tags);
            var hook = AfterNextUpdateTags;
            AfterNextUpdateTags = null;
            if (updated) hook?.Invoke();
            return updated;
        }

        public bool TryGet(DocumentUri uri, out DocumentBuffer? buffer) => inner.TryGet(uri, out buffer);

        public void Remove(DocumentUri uri) => inner.Remove(uri);

        public IEnumerable<DocumentBuffer> All => inner.All;
    }
}
