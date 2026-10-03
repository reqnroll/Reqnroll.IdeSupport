#nullable enable

using Gherkin.Ast;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.DocumentLinks;
using Reqnroll.IdeSupport.LSP.Server.Parsing;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.DocumentLinks;

public class DocumentLinkHandlerTests
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
    private readonly IParseCoordinator _parseCoordinator = Substitute.For<IParseCoordinator>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    // Line 0: "Feature: F\n" (11 chars), line 1: "@issue:1234 @smoke\n", line 2: "Scenario: S\n"
    private const string FeatureText = "Feature: F\n@issue:1234 @smoke\nScenario: S\n";

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly LspTextSnapshot Snapshot = new(FeatureUri.ToString(), 1, FeatureText);

    private static IdeSupportTag TagAt(int offset, string name) => new(
        IdeSupportTagTypes.Tag,
        new GherkinRange(Snapshot, offset, name.Length),
        new Tag(new Gherkin.Ast.Location(2, 1), name));

    private static readonly IdeSupportTag IssueTag = TagAt(11, "@issue:1234");
    private static readonly IdeSupportTag SmokeTag = TagAt(23, "@smoke");

    private DocumentLinkHandler CreateSut()
    {
        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>()).Returns(_configProvider);
        return new DocumentLinkHandler(_bufferService, _scopeManager, _parseCoordinator, _logger);
    }

    private static DocumentLinkParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private void SetupBuffer(IReadOnlyCollection<IdeSupportTag>? tags)
    {
        var buf = new DocumentBuffer(FeatureUri, 1, FeatureText, tags);
        DocumentBuffer? outBuf;
        _bufferService.TryGet(FeatureUri, out outBuf)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    private void SetupTagLinks(params (string Pattern, string Template)[] links)
    {
        var configuration = new IdeSupportConfiguration();
        configuration.Traceability.TagLinks = links
            .Select(l => new TagLinkConfiguration { TagPattern = l.Pattern, UrlTemplate = l.Template })
            .ToArray();
        configuration.CheckConfiguration();
        _configProvider.GetConfiguration().Returns(configuration);
    }

    [Fact]
    public async Task Returns_empty_when_buffer_not_found_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_tags_not_yet_computed_Async()
    {
        SetupBuffer(tags: null);

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_no_tag_links_configured_Async()
    {
        SetupBuffer(new[] { IssueTag, SmokeTag });
        SetupTagLinks();

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_link_only_for_tags_matching_a_configured_pattern_Async()
    {
        SetupBuffer(new[] { IssueTag, SmokeTag });
        SetupTagLinks((@"issue\:(?<id>\d+)", "https://github.com/specsolutions/my-project/issues/{id}"));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        var link = result.Should().ContainSingle().Subject;
        link.Target!.ToString().Should().Be("https://github.com/specsolutions/my-project/issues/1234");
        link.Range.Start.Should().Be(new Position(1, 0));
        link.Range.End.Should().Be(new Position(1, 11));
    }

    [Fact]
    public async Task Ignores_non_tag_nodes_Async()
    {
        var stepLike = new IdeSupportTag(IdeSupportTagTypes.StepBlock, new GherkinRange(Snapshot, 11, 11));
        SetupBuffer(new[] { stepLike });
        SetupTagLinks((".*", "https://example.com/{x}"));

        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().BeEmpty();
    }
}
