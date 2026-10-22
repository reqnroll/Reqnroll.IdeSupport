using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
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
/// The tagger parses a feature file with the default feature language configured for the project that owns
/// it (reqnroll.json "language.feature"), unless the file carries its own "# language:" directive.
/// </summary>
public sealed class GherkinDocumentTaggerProjectLanguageTests : IDisposable
{
    private const string FrenchFeature =
        "Fonctionnalité: Une fonctionnalité\n\nScénario: Un scénario\n    Etant donné une précondition\n    Quand une action\n    Alors un résultat\n";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rnr-lang-" + Guid.NewGuid().ToString("N"));

    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IBindingMatchService _bindingMatchService = Substitute.For<IBindingMatchService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();

    public GherkinDocumentTaggerProjectLanguageTests()
    {
        Directory.CreateDirectory(_root);
        _parser = new RecordingTagParser(TaggerTestParser.Create(_logger));
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>()).Returns(ProjectBindingRegistry.Invalid);
        _bufferService.UpdateTags(Arg.Any<DocumentUri>(), Arg.Any<IReadOnlyCollection<IdeSupportTag>>())
            .Returns(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private LspReqnrollProject CreateProject(string name, string? featureLanguage)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        if (featureLanguage != null)
            File.WriteAllText(Path.Combine(folder, "reqnroll.json"),
                $"{{ \"language\": {{ \"feature\": \"{featureLanguage}\" }} }}");

        return new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder = _root,
                ProjectFile = Path.Combine(folder, name + ".csproj"),
                ProjectFolder = folder,
                OutputAssemblyPath = Path.Combine(folder, "bin", name + ".dll"),
                TargetFrameworkMoniker = "net10.0"
            },
            new LspIdeScope(_logger));
    }

    private readonly RecordingTagParser _parser;

    private GherkinDocumentTaggerService CreateSut() =>
        new(_bufferService, _parser, _registryLookup,
            Substitute.For<ISemanticTokensService>(), _bindingMatchService, _scopeManager, _logger, _fileSystem);

    private void OpenBuffer(DocumentUri uri, string text)
    {
        var buffer = new DocumentBuffer(uri, 1, text);
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored).Returns(x => { x[1] = buffer; return true; });
    }

    private static IdeSupportGherkinDocument GetDocument(IEnumerable<IdeSupportTag> tags) =>
        (IdeSupportGherkinDocument)tags.First(t => t.Type == IdeSupportTagTypes.Document).Data!;

    [Fact]
    public async Task Open_file_without_language_directive_is_parsed_with_the_projects_configured_language()
    {
        var project = CreateProject("French", "fr");
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(project.ProjectFolder, "a.feature"));
        _scopeManager.ResolvePrimaryOwner(uri).Returns(project);
        OpenBuffer(uri, FrenchFeature);

        var tags = await CreateSut().ParseAsync(uri, 1);

        tags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        var document = GetDocument(tags);
        document.Feature.Should().NotBeNull();
        document.Feature!.Name.Should().Be("Une fonctionnalité");
        document.Feature.Children.OfType<Gherkin.Ast.Scenario>().Single().Steps.Should().HaveCount(3);
    }

    [Fact]
    public async Task Each_project_parses_with_its_own_configured_language()
    {
        var french = CreateProject("French", "fr");
        var english = CreateProject("English", null);
        var frenchUri = DocumentUri.FromFileSystemPath(Path.Combine(french.ProjectFolder, "a.feature"));
        var englishUri = DocumentUri.FromFileSystemPath(Path.Combine(english.ProjectFolder, "b.feature"));
        _scopeManager.ResolvePrimaryOwner(frenchUri).Returns(french);
        _scopeManager.ResolvePrimaryOwner(englishUri).Returns(english);
        OpenBuffer(frenchUri, FrenchFeature);
        var sut = CreateSut();

        var frenchTags = await sut.ParseAsync(frenchUri, 1);
        OpenBuffer(englishUri, "Feature: F\n\nScenario: S\n    Given a step\n");
        var englishTags = await sut.ParseAsync(englishUri, 1);

        frenchTags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        englishTags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        GetDocument(englishTags).Feature!.Name.Should().Be("F");
    }

    [Fact]
    public async Task Linked_file_outside_the_project_folder_uses_the_primary_owners_language()
    {
        var project = CreateProject("French", "fr");
        var linkedUri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "shared", "linked.feature"));
        _scopeManager.ResolvePrimaryOwner(linkedUri).Returns(project);
        OpenBuffer(linkedUri, FrenchFeature);

        var tags = await CreateSut().ParseAsync(linkedUri, 1);

        tags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        GetDocument(tags).Feature!.Name.Should().Be("Une fonctionnalité");
    }

    [Fact]
    public async Task Closed_file_scan_uses_the_language_of_the_project_being_scanned()
    {
        var french = CreateProject("French", "fr");
        var english = CreateProject("English", null);
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "shared", "shared.feature"));
        var sut = CreateSut();

        await sut.ScanClosedFileAsync(uri, FrenchFeature, french);
        var frenchTags = _parser.LastTags;
        await sut.ScanClosedFileAsync(uri, FrenchFeature, english);
        var englishTags = _parser.LastTags;

        // Only the French-configured owner understands the French document.
        frenchTags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        GetDocument(frenchTags).Feature!.Name.Should().Be("Une fonctionnalité");
        englishTags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Files_outside_any_project_default_to_english()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_root, "loose.feature"));
        _scopeManager.ResolvePrimaryOwner(uri).Returns((LspReqnrollProject?)null);
        OpenBuffer(uri, "Feature: F\n\nScenario: S\n    Given a step\n");

        var tags = await CreateSut().ParseAsync(uri, 1);

        tags.Where(t => t.Type == IdeSupportTagTypes.ParserError).Should().BeEmpty();
        GetDocument(tags).Feature!.Name.Should().Be("F");
    }

    private sealed class RecordingTagParser(IIdeSupportTagParser inner) : IIdeSupportTagParser
    {
        public IReadOnlyCollection<IdeSupportTag> LastTags { get; private set; } = Array.Empty<IdeSupportTag>();

        public IReadOnlyCollection<IdeSupportTag> Parse(
            Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot fileSnapshot,
            ProjectBindingRegistry bindingRegistry)
            => LastTags = inner.Parse(fileSnapshot, bindingRegistry);
    }
}
