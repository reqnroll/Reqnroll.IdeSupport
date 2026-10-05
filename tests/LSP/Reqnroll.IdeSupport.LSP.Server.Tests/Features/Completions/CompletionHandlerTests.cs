using System.Diagnostics;
using Gherkin;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.Completions;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using CompletionContext = Reqnroll.IdeSupport.LSP.Core.Completions.CompletionContext;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Completions;

public class CompletionHandlerTests
{
    private readonly ICompletionContextResolver _contextResolver = Substitute.For<ICompletionContextResolver>();
    private readonly ICompletionService _completionService = Substitute.For<ICompletionService>();
    private readonly ICompletionMatcher _matcher = Substitute.For<ICompletionMatcher>();
    private readonly IBindingMatchService _matchService = Substitute.For<IBindingMatchService>();
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IFeatureTagIndex _tagIndex = Substitute.For<IFeatureTagIndex>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    public CompletionHandlerTests()
    {
        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>()).Returns(_configProvider);
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
    }

    private CompletionHandler CreateSut(bool isVisualStudio = false, IOperationDurationRecorder? recorder = null) =>
            new(
                _contextResolver,
                _completionService,
                _matcher,
                _matchService,
                _bufferService,
                _scopeManager,
                _registryLookup,
                _tagIndex,
                new ClientIdeContext(isVisualStudio ? "visualstudio" : "vscode"),
                _logger,
                recorder);

    /// <summary>Programs the substituted tag pipeline to return <paramref name="labels"/> as the tag-group entries.</summary>
    private void SetupTags(IReadOnlyList<string> labels)
    {
        _tagIndex.GetTagCandidatesAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new StepCandidate("@smoke", 1) });
        _completionService.GetTagCompletions(
                Arg.Any<IReadOnlyCollection<StepCandidate>>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<string>(),
                _matcher)
            .Returns(new CompletionResult(labels.Select(l => new CompletionEntry(l, null, CompletionEntryKind.Keyword)).ToArray()));
    }

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

    private async Task<CompletionList> RequestStepCompletionAsync()
    {
        SetupBuffer(FeatureUri, "Feature: F\n  Scenario: S\n    Given my\n");
        var step = new IdeSupportGherkinStep(
            new Gherkin.Ast.Location(3, 5), "Given ", StepKeywordType.Context, "my", null!,
            StepKeyword.Given, ScenarioBlock.Given);
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new StepCompletionContext(step, "my", 10));
        _completionService.GetStepCompletions(
                Arg.Any<IdeSupportGherkinStep>(), Arg.Any<string>(), Arg.Any<ProjectBindingRegistry>(),
                Arg.Any<Func<ProjectStepDefinitionBinding, int>>(), _matcher)
            .Returns(new CompletionResult(new[]
            {
                new CompletionEntry("my step", null, CompletionEntryKind.Keyword),
                new CompletionEntry("my other step", null, CompletionEntryKind.Keyword),
            }));

        return await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(2, 12) },
            CancellationToken.None);
    }

    [Fact]
    public async Task Step_items_carry_the_accept_command_Async()
    {
        var result = await RequestStepCompletionAsync();

        result.Items.Should().HaveCount(2)
            .And.OnlyContain(i => i.Command != null && i.Command.Name == CompletionAcceptedHandler.CommandName);
    }

    [Fact]
    public async Task Keyword_items_never_carry_the_accept_command_Async()
    {
        SetupBuffer(FeatureUri, "Feature: F\n  Sc\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.ScenarioLine }));
        _completionService.GetKeywordCompletions(Arg.Any<TokenType[]>(), dialect)
            .Returns(new CompletionResult(new[] { new CompletionEntry("Scenario:", null, CompletionEntryKind.Keyword) }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(1, 4) },
            CancellationToken.None);

        result.Items.Should().NotBeEmpty().And.OnlyContain(i => i.Command == null);
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
            new CompletionEntry("Szenario: ", null, CompletionEntryKind.Keyword)
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
            new CompletionEntry("Given ", null, CompletionEntryKind.Keyword)
        }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, cursorChar) },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.End.Character.Should().BeLessThanOrEqualTo(cursorChar);
    }

    // ── Stale / out-of-range request position (issue #871) ─────────────────────

    [Theory]
    [InlineData("  Giv", 40, 5)]   // character far beyond the end of the line
    [InlineData("", 3, 0)]         // empty line (last line, no trailing newline)
    [InlineData("  Giv", 5, 5)]    // caret exactly at end of line
    [InlineData("", -2, 0)]        // negative character
    public async Task Keyword_completion_with_out_of_range_position_does_not_throw_Async(
        string line, int cursorChar, int expectedEnd)
    {
        SetupBuffer(FeatureUri, line);
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));
        _completionService.GetDefaultKeywordCompletions(dialect).Returns(new CompletionResult(new[]
        {
            new CompletionEntry("Given ", null, CompletionEntryKind.Keyword)
        }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, cursorChar) },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.End.Character.Should().Be(expectedEnd);
        range.Start.Character.Should().BeLessThanOrEqualTo(range.End.Character);
    }

    // ── Caret in the indentation of a line with content (deleted line above) ───

    private void SetupAnyKeywordContext()
    {
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, Array.Empty<TokenType>()));
        _completionService.GetDefaultKeywordCompletions(dialect).Returns(new CompletionResult(new[]
        {
            new CompletionEntry("Given ", null, CompletionEntryKind.Keyword)
        }));
    }

    [Theory]
    [InlineData("    Given a step", 0)]               // step, caret at column 0
    [InlineData("    Given a step", 2)]               // step, caret part-way through the indentation
    [InlineData("    Given a step", 4)]               // step, caret directly before the keyword
    [InlineData("Feature: Something", 0)]
    [InlineData("  Rule: A rule", 0)]
    [InlineData("  Background:", 0)]
    [InlineData("  Scenario: A scenario", 0)]
    [InlineData("  Scenario Outline: An outline", 0)]
    [InlineData("    Examples:", 0)]
    [InlineData("  @smoke", 0)]                       // tag line (tag completion must not fire either)
    [InlineData("  @smoke @slow", 0)]
    [InlineData("  # a comment", 0)]
    [InlineData("      | a | b |", 0)]                // table row (VS would otherwise get the safe item)
    [InlineData("    \"\"\"", 0)]                    // doc string fence
    [InlineData("  # language: en", 0)]
    [InlineData("\tGiven a step", 0)]                 // tab indentation
    [InlineData("Given a step", 0)]                   // no indentation at all
    public async Task Keyword_completion_is_suppressed_when_the_caret_is_before_the_content_of_a_line_Async(
        string line, int cursorChar)
    {
        SetupBuffer(FeatureUri, "Feature: F\n" + line + "\n");
        SetupAnyKeywordContext();
        SetupTags(new[] { "@smoke" });

        var result = await CreateSut().Handle(
            new CompletionParams
            {
                TextDocument = FeatureUri,
                Position = new Position(1, cursorChar),
                Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.TriggerCharacter,
                    TriggerCharacter = " "
                }
            },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        _contextResolver.DidNotReceiveWithAnyArgs().Resolve(default!, default, default, default!, default!);
    }

    [Theory]
    [InlineData("\n")]   // Backspace joining a blank line onto the prior step line
    [InlineData("\r\n")]
    [InlineData("R")]    // deleted letter
    [InlineData(" ")]    // deleted space
    public async Task Completion_requested_after_a_deletion_is_suppressed_Async(string deletedText)
    {
        // VS reports the deleted text as triggerCharacter, with triggerKind Invoked. The caret is
        // at the end of a complete step line, where step completion would otherwise pop up.
        SetupBuffer(FeatureUri, "Feature: F\n  Scenario: S\n    Given a step");
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(ci => throw new InvalidOperationException("must not resolve a context"));

        var result = await CreateSut(isVisualStudio: true).Handle(
            new CompletionParams
            {
                TextDocument = FeatureUri,
                Position = new Position(2, 16),
                Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.Invoked,
                    TriggerCharacter = deletedText
                }
            },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Invoked_request_with_a_trigger_character_is_not_treated_as_a_deletion_by_other_clients_Async()
    {
        SetupBuffer(FeatureUri, "Feature: F\n  Scenario: S\n    Giv");
        SetupAnyKeywordContext();

        var result = await CreateSut(isVisualStudio: false).Handle(
            new CompletionParams
            {
                TextDocument = FeatureUri,
                Position = new Position(2, 7),
                Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.Invoked,
                    TriggerCharacter = "\n"
                }
            },
            CancellationToken.None);

        result.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Completion_for_a_typed_trigger_character_is_not_treated_as_a_deletion_Async()
    {
        SetupBuffer(FeatureUri, " ");
        SetupAnyKeywordContext();

        var result = await CreateSut(isVisualStudio: true).Handle(
            new CompletionParams
            {
                TextDocument = FeatureUri,
                Position = new Position(0, 1),
                Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.TriggerCharacter,
                    TriggerCharacter = " "
                }
            },
            CancellationToken.None);

        result.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Table_row_with_the_caret_before_its_content_gets_no_visual_studio_safe_item_Async()
    {
        // The VS no-op "| " item exists so VS does not revert a typed '|'; a caret in the
        // indentation of a row is not that case, and the item would pop the list up.
        SetupBuffer(FeatureUri, "Feature: F\n      | a | b |\n");

        var result = await CreateSut(isVisualStudio: true).Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(1, 0) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("    Giv", 7)]    // typing a keyword after indentation
    [InlineData("    ", 4)]       // whitespace-only line, caret at its end
    [InlineData("    ", 0)]       // whitespace-only line, caret at column 0
    [InlineData("", 0)]           // empty line
    [InlineData("Gi", 2)]         // caret after the typed prefix
    public async Task Keyword_completion_is_still_offered_when_the_caret_is_not_before_line_content_Async(
        string line, int cursorChar)
    {
        SetupBuffer(FeatureUri, line);
        SetupAnyKeywordContext();

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, cursorChar) },
            CancellationToken.None);

        result.Items.Should().NotBeEmpty();
    }

    // ── Group-wise narrowing by typed prefix (issue #818 follow-up) ────────────

    [Fact]
    public async Task Typed_at_sign_excludes_token_groups_with_no_matching_member_Async()
    {
        // Both TagLine and ScenarioLine are expected here (e.g. a blank line before a Feature/
        // Scenario), but only "@" has been typed so far -- the ScenarioLine group has no member
        // starting with "@", so it must be dropped entirely, leaving only the tag candidates.
        SetupBuffer(FeatureUri, "@\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        var tokens = new[] { TokenType.TagLine, TokenType.ScenarioLine };
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, tokens));
        SetupTags(new[] { "@ignore", "@smoke" });
        _completionService.GetKeywordCompletions(
                Arg.Is<TokenType[]>(t => t.SequenceEqual(new[] { TokenType.ScenarioLine })), dialect)
            .Returns(new CompletionResult(new[] { new CompletionEntry("Scenario: ", null, CompletionEntryKind.Keyword) }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        result.Items.Select(i => i.Label).Should().BeEquivalentTo("@ignore", "@smoke");
        _ = _tagIndex.Received(1).GetTagCandidatesAsync(FeatureUri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Typed_prefix_keeps_a_matching_group_whole_rather_than_trimming_to_one_entry_Async()
    {
        // Typing "Gi" must still offer Given *and* When *and* Then together -- deciding which
        // one is the client's own job via its list filtering -- because the StepLine group has
        // at least one matching member ("Given "). Narrowing is by whole group, not per entry.
        SetupBuffer(FeatureUri, "    Gi\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        var tokens = new[] { TokenType.StepLine };
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, tokens));
        _completionService.GetKeywordCompletions(
                Arg.Is<TokenType[]>(t => t.SequenceEqual(new[] { TokenType.StepLine })), dialect)
            .Returns(new CompletionResult(new[]
            {
                    new CompletionEntry("Given ", null, CompletionEntryKind.Keyword),
                    new CompletionEntry("When ",  null, CompletionEntryKind.Keyword),
                    new CompletionEntry("Then ",  null, CompletionEntryKind.Keyword),
            }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 6) },
            CancellationToken.None);

        result.Items.Select(i => i.Label).Should().BeEquivalentTo("Given ", "When ", "Then ");
    }

    // ── Tag completion (issue #828) ──────────────────────────────────────────

    [Fact]
    public async Task A_second_tag_typed_after_a_completed_tag_offers_tag_completions_Async()
    {
        // Tag positions are completed in any state — including a second tag right after a
        // completed first one ("@tag1 @") — per the issue #828 design decision that reversed the
        // merged #818 suppression for tag lines.
        SetupBuffer(FeatureUri, "@tag1 @\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 7) },
            CancellationToken.None);

        result.Items.Select(i => i.Label).Should().Contain("@ignore");
    }

    [Fact]
    public async Task A_second_tag_completion_range_spares_the_already_typed_first_tag_Async()
    {
        // Accepting a tag on a line that already carries a completed tag ("@tag1 @") must only
        // replace the in-progress tag (after the last whitespace), never swallow the first tag —
        // the keyword range (line start → caret) would delete "@tag1 ".
        SetupBuffer(FeatureUri, "@tag1 @\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 7) },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.Start.Should().Be(new Position(0, 6), "the replacement starts after the completed first tag");
        range.End.Should().Be(new Position(0, 7), "the replacement stops at the caret");
    }

    [Fact]
    public void Registration_declares_at_and_space_as_completion_trigger_characters()
    {
        // Clients only request completion on identifier characters (and backspace/delete) unless a
        // trigger character is declared: without " " no popup follows the space that separates two
        // tags, or a space typed on a blank line before a tag (issue #828 follow-up).
        var options = CreateSut().GetRegistrationOptions(new CompletionCapability(), new ClientCapabilities());

        options.TriggerCharacters.Should().BeEquivalentTo(new[] { "@", " " });
    }

    [Fact]
    public async Task A_space_after_a_completed_tag_offers_tags_with_an_empty_replacement_range_Async()
    {
        SetupBuffer(FeatureUri, "@smoke \n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });

        var result = await CreateSut().Handle(
            new CompletionParams
            {
                TextDocument = FeatureUri,
                Position = new Position(0, 7),
                Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.TriggerCharacter,
                    TriggerCharacter = " "
                }
            },
            CancellationToken.None);

        var range = result.Items.Should().ContainSingle().Subject.TextEdit!.TextEdit!.Range;
        range.Start.Should().Be(new Position(0, 7));
        range.End.Should().Be(new Position(0, 7));
    }

    [Theory]
    [InlineData("Scenario ", 9)]
    [InlineData("z ", 2)]
    [InlineData("@smoke Feature ", 15)]
    public async Task Tags_are_not_offered_after_a_space_that_follows_non_tag_text_Async(string line, int caret)
    {
        // A space is a completion trigger, so "Scenario " (mid-keyword) or "z " reaches the
        // handler with an empty in-progress word on a tag-legal line. Tags must still not be
        // offered: a tag line holds tags only, so text before the word rules tags out.
        SetupBuffer(FeatureUri, line + "\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, caret) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        _ = _tagIndex.DidNotReceive().GetTagCandidatesAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_tag_branch_is_timed_under_its_own_perf_label_Async()
    {
        SetupBuffer(FeatureUri, "@\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });
        var recorder = Substitute.For<IOperationDurationRecorder>();

        await CreateSut(recorder: recorder).Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        recorder.Received(1).Measure("textDocument/completion#tag", Arg.Any<DocumentUri?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task A_failing_tag_index_degrades_to_built_in_tags_and_logs_a_warning_Async()
    {
        // The project index throwing (e.g. an unexpected parser/IO fault) must not fail the whole
        // completion request: the popup still appears, with the built-in tags.
        SetupBuffer(FeatureUri, "@\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });
        _tagIndex.GetTagCandidatesAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyCollection<StepCandidate>>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        result.Items.Select(i => i.Label).Should().Contain("@ignore");
        _logger.Received().Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Warning && m.Message.Contains("boom")));
    }

    [Fact]
    public async Task Tags_already_typed_on_the_completing_line_are_passed_as_exclusions_Async()
    {
        SetupBuffer(FeatureUri, "@smoke @\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));
        SetupTags(new[] { "@ignore" });

        await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 8) },
            CancellationToken.None);

        _completionService.Received(1).GetTagCompletions(
            Arg.Any<IReadOnlyCollection<StepCandidate>>(),
            Arg.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "@smoke", "@" })),
            "",
            _matcher);
    }

    [Fact]
    public async Task Tag_completions_are_not_offered_when_the_typed_word_does_not_start_with_at_Async()
    {
        // A tag-position line whose in-progress word doesn't start with "@" (e.g. "z" typed on a
        // tag-capable blank line) must not offer tags — it can only ever become a keyword, not a
        // tag. The tag pipeline must not be consulted at all.
        SetupBuffer(FeatureUri, "z\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.TagLine }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        _ = _tagIndex.DidNotReceive().GetTagCandidatesAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>());
        _completionService.DidNotReceive().GetTagCompletions(
            Arg.Any<IReadOnlyCollection<StepCandidate>>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<string>(), _matcher);
    }

    [Fact]
    public async Task Tag_completions_are_never_offered_on_non_tag_positions_Async()
    {
        // A position whose expected tokens don't include TagLine (e.g. a step line) must never
        // trigger the tag pipeline, even when the context has "line text starts with @" shape.
        SetupBuffer(FeatureUri, "@\n");
        var dialect = new GherkinDialectProvider("en").DefaultDialect;
        _contextResolver.Resolve(
            Arg.Any<Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ProjectBindingRegistry>(), Arg.Any<string>())
            .Returns(new KeywordCompletionContext(dialect, new[] { TokenType.ScenarioLine }));
        _completionService.GetKeywordCompletions(
                Arg.Is<TokenType[]>(t => t.SequenceEqual(new[] { TokenType.ScenarioLine })), dialect)
            .Returns(new CompletionResult(new[] { new CompletionEntry("Scenario: ", null, CompletionEntryKind.Keyword) }));

        var result = await CreateSut().Handle(
            new CompletionParams { TextDocument = FeatureUri, Position = new Position(0, 1) },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        _ = _tagIndex.DidNotReceive().GetTagCandidatesAsync(Arg.Any<DocumentUri>(), Arg.Any<CancellationToken>());
    }
}
