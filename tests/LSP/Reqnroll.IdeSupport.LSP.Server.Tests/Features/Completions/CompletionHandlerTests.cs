using Gherkin;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.Completions;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using CompletionContext = Reqnroll.IdeSupport.LSP.Core.Completions.CompletionContext;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Completions;

public class CompletionHandlerTests
{
    private readonly ICompletionContextResolver    _contextResolver = Substitute.For<ICompletionContextResolver>();
    private readonly ICompletionService            _completionService = Substitute.For<ICompletionService>();
    private readonly ICompletionMatcher             _matcher = Substitute.For<ICompletionMatcher>();
    private readonly IBindingMatchService           _matchService = Substitute.For<IBindingMatchService>();
    private readonly IDocumentBufferService        _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    public CompletionHandlerTests()
    {
        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>()).Returns(_configProvider);
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
    }

    private CompletionHandler CreateSut(bool isVisualStudio = false) =>
        new(
            _contextResolver,
            _completionService,
            _matcher,
            _matchService,
            _bufferService,
            _scopeManager,
            _registryLookup,
            new ClientIdeContext(isVisualStudio ? "visualstudio" : "vscode"),
            _logger);

    private void SetupBuffer(DocumentUri uri, string text)
    {
        var buf = new DocumentBuffer(uri, 1, text);
        DocumentBuffer? outBuf;
        _bufferService.TryGet(uri, out outBuf).Returns(x => { x[1] = buf; return true; });
    }

    [Fact]
    public async Task Returns_an_empty_list_for_a_non_feature_uri_Async()
    {
        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = CsUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        _bufferService.DidNotReceive().TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>());
    }

    [Fact]
    public async Task Returns_an_empty_list_when_there_is_no_document_buffer_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_an_empty_list_when_the_context_resolver_finds_no_completion_appropriate_Async()
    {
        SetupBuffer(FeatureUri, "Feature: F\n");
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns((CompletionContext?)null);

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 0) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task VS_gets_a_fake_cell_separator_item_for_a_suppressed_table_row_completion_Async()
    {
        SetupBuffer(FeatureUri, "    |4|\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));

        var result = await CreateSut(isVisualStudio: true).Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 5) },
            CancellationToken.None);

        result.Items.Should().ContainSingle(i => i.Label == "| ");
    }

    [Fact]
    public async Task Non_VS_clients_get_an_empty_list_for_a_suppressed_table_row_completion_Async()
    {
        SetupBuffer(FeatureUri, "    |4|\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));

        var result = await CreateSut(isVisualStudio: false).Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 5) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    // ── Keyword completion range (issue #561) ─────────────────────────────────

    [Fact]
    public async Task Keyword_completion_textEdit_range_stops_at_the_cursor_not_at_the_end_of_the_line_Async()
    {
        // "Szenario:[scenario name]" with the caret right after the "S" of "Szenario" (char 1) --
        // the exact repro from issue #561. The line has real text well beyond the caret; the old
        // whole-trimmed-line range would delete all of it when a keyword was accepted.
        const string line = "Szenario:[scenario name]";
        SetupBuffer(FeatureUri, line + "\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));
        _completionService.GetDefaultKeywordCompletions(dialect).Returns(new CompletionResult(new[]
        {
            new CompletionEntry("Grundlage: ", null, CompletionEntryKind.Keyword)
        }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.Start.Should().Be(new Position(0, 0));
        range.End.Should().Be(new Position(0, 1), "the replacement must stop at the caret, never touch text to its right");
    }

    [Fact]
    public async Task Keyword_completion_textEdit_range_end_never_exceeds_the_request_position_Async()
    {
        // General assertion the issue itself asks for, independent of the specific repro above.
        const string line = "  Given [context] and more text after the caret";
        SetupBuffer(FeatureUri, line + "\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        var cursorChar = 8; // just after "Given"
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));
        _completionService.GetDefaultKeywordCompletions(dialect).Returns(new CompletionResult(new[]
        {
            new CompletionEntry("When ", null, CompletionEntryKind.Keyword)
        }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, cursorChar) },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.End.Character.Should().BeLessThanOrEqualTo(cursorChar);
    }
}
