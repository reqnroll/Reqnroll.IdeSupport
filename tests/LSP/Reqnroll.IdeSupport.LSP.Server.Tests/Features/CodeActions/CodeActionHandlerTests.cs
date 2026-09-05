using Gherkin;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;


using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;



using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

public class CodeActionHandlerTests
{
    private readonly BindingMatchService         _matchService  = new();
    private readonly IStepScaffoldService        _scaffoldService = new StepScaffoldService();
    private readonly ILspWorkspaceScopeManager   _scopeManager  = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDocumentBufferService      _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IIdeSupportLogger             _logger        = Substitute.For<IIdeSupportLogger>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
    private readonly ILspTelemetryService        _telemetryService = Substitute.For<ILspTelemetryService>();
    private readonly IFileSystemForIDE           _fileSystem = new FileSystemForIDE();
    private readonly ICompletionService          _completionService = new CompletionService();
    private readonly IErrorTelemetryService      _errorTelemetryService = Substitute.For<IErrorTelemetryService>();

    private const string FeatureText =
        "Feature: F\nScenario: S\n    Given a step\n    When I press add\n";

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public CodeActionHandlerTests()
    {
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>())
                     .Returns((LspReqnrollProject?)null);

        _scopeManager.GetConfigurationProviderForUri(Arg.Any<DocumentUri>())
                     .Returns(_configProvider);

        _configProvider.GetConfiguration()
                       .Returns(new IdeSupportConfiguration());
    }

    // Defaults to VS Code so existing tests (written before the #563 follow-up VS-Code-only gate)
    // keep exercising the ambiguous-step "Go to" actions without each having to opt in.
    private CodeActionHandler CreateSut(ClientIdeContext? clientIde = null) =>
        new(_matchService, _scaffoldService, _scopeManager, _bufferService, _logger, _fileSystem,
            _completionService, _errorTelemetryService, clientIde ?? new ClientIdeContext("vscode"),
            _telemetryService);

    private static CodeActionParams RequestAt(
        DocumentUri uri, int line = 0, int character = 0, CodeActionContext? context = null) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Range = new LspRange(new Position(line, character), new Position(line, character)),
            Context = context ?? new CodeActionContext { Diagnostics = new Container<Diagnostic>() }
        };

    // ── Guard rails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_null_for_non_feature_URI()
    {
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var sut   = CreateSut();

        var result = await sut.Handle(RequestAt(csUri), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_no_undefined_steps()
    {
        // No match set registered → all steps appear undefined but the cache returns empty
        var sut    = CreateSut();
        var result = await sut.Handle(RequestAt(FeatureUri), CancellationToken.None);

        // Nothing in the match service → no actions
        result.Should().BeEmpty();
    }

    // ── With undefined steps ──────────────────────────────────────────────────

    [Fact]
    public async Task Returns_define_all_action_for_single_undefined_step()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        var actions = result!.ToList();
        actions.Should().HaveCount(1);
        var action = actions[0].CodeAction!;
        action.Title.Should().Be("Define missing step");
        action.Kind.Should().Be(CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task Does_not_offer_Define_action_when_cursor_is_on_an_ambiguous_step()
    {
        // Line 2 ("    Given a step") is ambiguous; line 3 ("    When I press add") is a
        // genuinely undefined step elsewhere in the same file. Invoking the lightbulb on the
        // ambiguous step must not offer to "define" the unrelated undefined step — that step has
        // nothing to do with what's under the cursor.
        SeedMatchService(
            AmbiguousMatch("a step", lineOffset: 23, length: 6),
            UndefinedMatch("I press add", ScenarioBlock.When, lineOffset: 41));

        var result = await CreateSut().Handle(RequestAt(FeatureUri, line: 2), CancellationToken.None);

        result!.Select(a => a.CodeAction!.Title).Should().NotContain(t => t.StartsWith("Define"));
    }

    // ── With an ambiguous step under the cursor (issue #563) ────────────────────

    [Fact]
    public async Task Offers_go_to_actions_for_each_competing_binding_of_an_ambiguous_step()
    {
        SeedMatchService(AmbiguousMatch("a step", lineOffset: 23, length: 6));

        var result = await CreateSut().Handle(RequestAt(FeatureUri, line: 2), CancellationToken.None);

        var actions = result!.Select(a => a.CodeAction!).ToList();
        actions.Should().HaveCount(2);
        actions.Should().Contain(a => a.Title.Contains("StepsA.Handle"));
        actions.Should().Contain(a => a.Title.Contains("StepsB.Handle"));
        actions.Should().AllSatisfy(a =>
        {
            a.Kind.Should().Be(CodeActionKind.QuickFix);
            a.Edit.Should().BeNull("navigation actions carry no edit, only a Command");
            a.Command.Should().NotBeNull();
            a.Diagnostics.Should().NotBeNullOrEmpty();
            a.Diagnostics!.Single().Source.Should().Be(DiagnosticsAggregator.BindingSource);
        });
    }

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("rider")]
    public async Task Does_not_offer_go_to_actions_for_non_VSCode_clients(string ide)
    {
        // A "Go to" action carries only a vscode.open Command, no Edit (issue #563 follow-up).
        // VS Code's LSP client recognizes that command name and runs it locally; Visual Studio's
        // and Rider's do not, and forwarding it to the server via workspace/executeCommand fails
        // ("Method not found" — confirmed live in VS, since neither client special-cases it the
        // way VS Code does), so the action would silently do nothing there.
        SeedMatchService(AmbiguousMatch("a step", lineOffset: 23, length: 6));

        var result = await CreateSut(new ClientIdeContext(ide))
            .Handle(RequestAt(FeatureUri, line: 2), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_define_all_action_for_multiple_undefined_steps()
    {
        SeedMatchService(
            UndefinedMatch("I press add",     ScenarioBlock.When),
            UndefinedMatch("the result is 4", ScenarioBlock.Then));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        var actions = result!.ToList();
        // Should include at minimum "Define all missing steps in file"
        actions.Should().HaveCountGreaterThanOrEqualTo(1);
        actions.Should().Contain(a =>
            a.CodeAction != null &&
            a.CodeAction.Title.Contains("Define all missing steps"));
    }

    [Fact]
    public async Task WorkspaceEdit_targets_new_cs_file_alongside_feature()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action = result!.First().CodeAction!;
        action.Edit.Should().NotBeNull();
        // The edit must reference a .cs file in the same folder as the feature
        var edits = action.Edit!.DocumentChanges!.ToList();
        edits.Should().NotBeEmpty();
        var textEdit = edits.FirstOrDefault(e => e.IsTextDocumentEdit);
        textEdit.Should().NotBeNull();
        textEdit!.TextDocumentEdit!.TextDocument.Uri.Path
            .Should().EndWith(".cs");
    }

    [Fact]
    public async Task Generated_file_content_contains_step_expression()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action = result!.First().CodeAction!;
        var edits  = action.Edit!.DocumentChanges!.ToList();
        var textEdit = edits.First(e => e.IsTextDocumentEdit);
        var newText = textEdit.TextDocumentEdit!.Edits.First().NewText;

        newText.Should().Contain("WhenIPressAdd");
        newText.Should().Contain("[Binding]");
        newText.Should().Contain("throw new PendingStepException();");
    }

    [Fact]
    public async Task Deduplicates_identical_step_expressions()
    {
        // Two undefined matches with exactly the same step text → one stub
        SeedMatchService(
            UndefinedMatch("I press add", ScenarioBlock.When),
            UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action   = result!.First().CodeAction!;
        var textEdit = action.Edit!.DocumentChanges!
            .First(e => e.IsTextDocumentEdit)
            .TextDocumentEdit!.Edits.First().NewText;

        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(textEdit, @"WhenIPressAdd").Count;
        occurrences.Should().Be(1);
    }

    [Fact]
    public async Task Uses_numeric_suffix_when_target_file_already_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath  = Path.Combine(tempDir, "calculator.feature");
            var featureUri   = DocumentUri.FromFileSystemPath(featurePath);
            var className    = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);

            // Pre-create the file the handler would normally target
            File.WriteAllText(Path.Combine(tempDir, className + ".cs"), "// existing");

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri, UndefinedMatch("I press add", ScenarioBlock.When, featureUri));

            var result = await CreateSut().Handle(RequestAt(featureUri), CancellationToken.None);

            var action  = result!.First().CodeAction!;
            var changes = action.Edit!.DocumentChanges!.ToList();

            var createOp  = changes.First(e => e.IsCreateFile).CreateFile!;
            var textDocOp = changes.First(e => e.IsTextDocumentEdit).TextDocumentEdit!;

            // Both operations must target the suffixed file, not the pre-existing one
            createOp.Uri.Path.Should().EndWith(className + "2.cs");
            textDocOp.TextDocument.Uri.Path.Should().EndWith(className + "2.cs");

            // The generated class name must match the file name to avoid a duplicate-class conflict
            var generatedText = textDocOp.Edits.First().NewText;
            generatedText.Should().Contain($"class {className}2");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Increments_suffix_until_free_name_is_found()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath = Path.Combine(tempDir, "calculator.feature");
            var featureUri  = DocumentUri.FromFileSystemPath(featurePath);
            var className   = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);

            // Pre-create both the base name and the "2" variant
            File.WriteAllText(Path.Combine(tempDir, className + ".cs"),  "// existing");
            File.WriteAllText(Path.Combine(tempDir, className + "2.cs"), "// existing 2");

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri, UndefinedMatch("I press add", ScenarioBlock.When, featureUri));

            var result = await CreateSut().Handle(RequestAt(featureUri), CancellationToken.None);

            var createOp = result!.First().CodeAction!
                .Edit!.DocumentChanges!.First(e => e.IsCreateFile).CreateFile!;

            createOp.Uri.Path.Should().EndWith(className + "3.cs");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Append-candidate targeting ───────────────────────────────────────────

    private const string ValidBindingClass =
        "using System;\r\nusing Reqnroll;\r\n\r\nnamespace Test;\r\n\r\n[Binding]\r\npublic class CandidateSteps\r\n{\r\n}\r\n";

    [Fact]
    public async Task Offers_append_action_alongside_new_file_when_a_candidate_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath  = Path.Combine(tempDir, "calculator.feature");
            var featureUri   = DocumentUri.FromFileSystemPath(featurePath);
            var candidatePath = Path.Combine(tempDir, "CandidateSteps.cs");
            File.WriteAllText(candidatePath, ValidBindingClass);

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri,
                DefinedMatch("a step", ScenarioBlock.Given, featureUri, candidatePath, lineOffset: 23),
                UndefinedMatch("I press add", ScenarioBlock.When, featureUri, lineOffset: 41));

            var result = await CreateSut().Handle(RequestAt(featureUri, line: 3), CancellationToken.None);

            var actions = result!.Select(a => a.CodeAction!).ToList();
            actions.Should().HaveCount(2);

            var appendAction = actions.Should().ContainSingle(a => a.Title.EndsWith("CandidateSteps.cs")).Subject;
            var newFileAction = actions.Should().ContainSingle(a => a.Title.EndsWith("new file")).Subject;

            // Append action: no CreateFile op, targets the existing candidate file, keeps its content.
            var appendChanges = appendAction.Edit!.DocumentChanges!.ToList();
            appendChanges.Should().NotContain(c => c.IsCreateFile);
            var appendEdit = appendChanges.Single(c => c.IsTextDocumentEdit).TextDocumentEdit!;
            appendEdit.TextDocument.Uri.Path.Should().Be(DocumentUri.FromFileSystemPath(candidatePath).Path);
            appendEdit.Edits.First().NewText.Should().Contain("WhenIPressAdd").And.Contain("[Binding]");

            // New-file action: still creates a brand-new file as before.
            var newFileChanges = newFileAction.Edit!.DocumentChanges!.ToList();
            newFileChanges.Should().Contain(c => c.IsCreateFile);

            // The append action is the intended default choice, so it — not the new-file
            // fallback — must carry IsPreferred: some clients (VS in particular) don't preserve
            // the server's array order in the lightbulb menu and lean on this signal instead.
            appendAction.IsPreferred.Should().BeTrue();
            newFileAction.IsPreferred.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Falls_back_to_plain_new_file_action_when_the_only_candidate_cannot_be_parsed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath  = Path.Combine(tempDir, "calculator.feature");
            var featureUri   = DocumentUri.FromFileSystemPath(featurePath);
            var candidatePath = Path.Combine(tempDir, "CandidateSteps.cs");
            // No "class" keyword at all -> AppendToFile bails out (returns null).
            File.WriteAllText(candidatePath, "// not a real step definition file\r\n");

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);
            SeedMatchServiceFor(featureUri,
                DefinedMatch("a step", ScenarioBlock.Given, featureUri, candidatePath, lineOffset: 23),
                UndefinedMatch("I press add", ScenarioBlock.When, featureUri, lineOffset: 41));

            var result = await CreateSut().Handle(RequestAt(featureUri, line: 3), CancellationToken.None);

            var actions = result!.Select(a => a.CodeAction!).ToList();
            actions.Should().HaveCount(1);
            actions[0].Title.Should().Be("Define missing step"); // plain title: only one target ever resolved
            actions[0].Edit!.DocumentChanges!.Should().Contain(c => c.IsCreateFile);
            actions[0].IsPreferred.Should().BeTrue(); // sole surviving action must still be preferred
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Caps_total_actions_at_six_when_many_candidates_exist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var featurePath = Path.Combine(tempDir, "calculator.feature");
            var featureUri  = DocumentUri.FromFileSystemPath(featurePath);

            var definedMatches = new List<StepBindingMatch>();
            for (int i = 0; i < 8; i++)
            {
                var candidatePath = Path.Combine(tempDir, $"CandidateSteps{i}.cs");
                File.WriteAllText(candidatePath, ValidBindingClass);
                // Give each candidate a distinct step count so they rank deterministically and all differ.
                // lineOffset is irrelevant to ranking here (only the source file matters), so reuse 0
                // for every match to stay within the short fixture feature text's bounds.
                for (int j = 0; j <= i; j++)
                    definedMatches.Add(DefinedMatch($"defined step {i}-{j}", ScenarioBlock.Given, featureUri, candidatePath, lineOffset: 0));
            }

            _scopeManager.GetConfigurationProviderForUri(featureUri).Returns(_configProvider);
            _scopeManager.ResolvePrimaryOwner(featureUri).Returns((LspReqnrollProject?)null);

            var allMatches = definedMatches.Append(UndefinedMatch("I press add", ScenarioBlock.When, featureUri, lineOffset: 41)).ToArray();
            SeedMatchServiceFor(featureUri, allMatches);

            var result = await CreateSut().Handle(RequestAt(featureUri, line: 3), CancellationToken.None);

            result!.Should().HaveCount(6); // 5 append candidates (MaxAppendCandidates) + 1 new-file fallback

            // Telemetry must reflect what was actually returned (post-cap), not the uncapped
            // count the builder produced internally.
            _telemetryService.Received(1).SendEvent(
                "DefineSteps command offered",
                Arg.Is<Dictionary<string, object?>>(p => (int)p["ActionsOffered"]! == 6));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Telemetry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sends_telemetry_when_a_define_steps_action_is_offered()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        _telemetryService.Received(1).SendEvent(
            "DefineSteps command offered",
            Arg.Is<Dictionary<string, object?>>(p =>
                (int)p["UndefinedStepCount"]! == 1 && (int)p["ActionsOffered"]! == 1));
    }

    [Fact]
    public async Task Does_not_send_telemetry_when_no_actions_are_offered()
    {
        await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        _telemetryService.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }

    // ── CodeAction.Diagnostics association (issue #563) ─────────────────────────

    [Fact]
    public async Task Define_missing_step_action_is_associated_with_the_undefined_step_diagnostic()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var result = await CreateSut().Handle(RequestAt(FeatureUri), CancellationToken.None);

        var action = result!.First().CodeAction!;
        action.Diagnostics.Should().NotBeNullOrEmpty();
        var diagnostic = action.Diagnostics!.Single();
        diagnostic.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diagnostic.Message.Should().Be(DiagnosticsAggregator.UndefinedStepMessage);
    }

    // ── Parser-error "Insert keyword" quick fixes (issue #563) ──────────────────

    // Known to produce a ParserError tag (mirrors IdeSupportTagParserTests.Malformed_feature_produces_ParserError_tag).
    private const string BrokenFeatureText = "not a feature file\nsome garbage\n";

    [Fact]
    public async Task Offers_insert_keyword_actions_for_a_parser_error_at_the_cursor()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/broken.feature");
        var tags = ParseTags(BrokenFeatureText);
        var errorTag = tags.First(t => t.Type == IdeSupportTagTypes.ParserError);
        var (errorLine, errorChar) = errorTag.Range.StartLinePosition;

        _scopeManager.ResolvePrimaryOwner(uri).Returns((LspReqnrollProject?)null);
        _scopeManager.GetConfigurationProviderForUri(uri).Returns(_configProvider);
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(uri, 1, BrokenFeatureText, tags);
                return true;
            });

        var result = await CreateSut().Handle(RequestAt(uri, errorLine, errorChar), CancellationToken.None);

        var actions = result!.Select(a => a.CodeAction!).ToList();
        actions.Should().NotBeEmpty();
        actions.Should().AllSatisfy(a =>
        {
            a.Title.Should().StartWith("Insert '");
            a.Kind.Should().Be(CodeActionKind.QuickFix);
            a.Edit.Should().NotBeNull();
            a.Diagnostics.Should().NotBeNullOrEmpty();
            a.Diagnostics!.Single().Source.Should().Be(DiagnosticsAggregator.ParserSource);

            // The edit must replace the whole flagged token, not splice text in before it — an
            // insert-only edit at the error's start position left the mistyped text in place
            // (e.g. a "Th" typo plus "Insert '\"\"\"'" produced the malformed `"""Th` rather than
            // replacing "Th" outright; confirmed live in VS, issue #563 follow-up).
            var edit = a.Edit!.DocumentChanges!.Single().TextDocumentEdit!.Edits.Single();
            edit.Range.Should().Be(errorTag.Range.ToLspRange());
        });
    }

    [Fact]
    public async Task Does_not_offer_insert_keyword_actions_away_from_the_parser_error()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/broken.feature");
        var tags = ParseTags(BrokenFeatureText);
        var snapshot = new LspTextSnapshot(uri.ToString(), 1, BrokenFeatureText);

        _scopeManager.ResolvePrimaryOwner(uri).Returns((LspReqnrollProject?)null);
        _scopeManager.GetConfigurationProviderForUri(uri).Returns(_configProvider);
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(uri, 1, BrokenFeatureText, tags);
                return true;
            });

        // The blank line after the final newline is always legal (Empty/EOF), so it can never be
        // inside a ParserError tag's span — a safe "away from any error" cursor position.
        var farLine = snapshot.LineCount - 1;
        var result = await CreateSut().Handle(RequestAt(uri, line: farLine), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // Mirrors the live-tested VS repro: a truncated "Th" (typing "Then") after a valid step.
    private const string TruncatedKeywordFeatureText =
        "Feature: F\nScenario: S\n    Given a step\n    When I press add\n    Th\n";

    [Fact]
    public async Task Marks_the_closest_matching_keyword_as_preferred()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/truncated.feature");
        var tags = ParseTags(TruncatedKeywordFeatureText);
        var errorTag = tags.First(t => t.Type == IdeSupportTagTypes.ParserError);
        var (errorLine, errorChar) = errorTag.Range.StartLinePosition;

        _scopeManager.ResolvePrimaryOwner(uri).Returns((LspReqnrollProject?)null);
        _scopeManager.GetConfigurationProviderForUri(uri).Returns(_configProvider);
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(uri, 1, TruncatedKeywordFeatureText, tags);
                return true;
            });

        var result = await CreateSut().Handle(RequestAt(uri, errorLine, errorChar), CancellationToken.None);

        var actions = result!.Select(a => a.CodeAction!).ToList();
        actions.Should().ContainSingle(a => a.IsPreferred == true)
            .Which.Title.Should().Be("Insert 'Then'");
        actions.Where(a => a.Title != "Insert 'Then'").Should().AllSatisfy(a => a.IsPreferred.Should().NotBe(true));
    }

    [Fact]
    public async Task Marks_no_action_preferred_when_nothing_was_typed_yet()
    {
        // BrokenFeatureText's error is on a line with no plausible keyword-typo text at all
        // ("not a feature file"), so no entry should win the closest-match heuristic.
        var uri = DocumentUri.FromFileSystemPath("/workspace/broken.feature");
        var tags = ParseTags(BrokenFeatureText);
        var errorTag = tags.First(t => t.Type == IdeSupportTagTypes.ParserError);
        var (errorLine, errorChar) = errorTag.Range.StartLinePosition;

        _scopeManager.ResolvePrimaryOwner(uri).Returns((LspReqnrollProject?)null);
        _scopeManager.GetConfigurationProviderForUri(uri).Returns(_configProvider);
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(uri, 1, BrokenFeatureText, tags);
                return true;
            });

        var result = await CreateSut().Handle(RequestAt(uri, errorLine, errorChar), CancellationToken.None);

        result!.Select(a => a.CodeAction!).Should().AllSatisfy(a => a.IsPreferred.Should().NotBe(true));
    }

    // ── Honouring context.only / context.diagnostics (issue #563) ───────────────

    [Fact]
    public async Task Returns_empty_when_context_only_excludes_QuickFix()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var context = new CodeActionContext
        {
            Diagnostics = new Container<Diagnostic>(),
            Only        = new Container<CodeActionKind>(CodeActionKind.Refactor)
        };

        var result = await CreateSut().Handle(RequestAt(FeatureUri, context: context), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_context_diagnostics_scoping_unrelated_to_any_offered_action()
    {
        SeedMatchService(UndefinedMatch("I press add", ScenarioBlock.When));

        var context = new CodeActionContext
        {
            Diagnostics = new Container<Diagnostic>(new Diagnostic
            {
                Range   = new LspRange(new Position(0, 0), new Position(0, 1)),
                Source  = "some.other.source",
                Message = "unrelated"
            })
        };

        var result = await CreateSut().Handle(RequestAt(FeatureUri, context: context), CancellationToken.None);

        // The request named a diagnostic that has nothing to do with the offered "Define missing
        // step" action, so it must be filtered out.
        result.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyCollection<IdeSupportTag> ParseTags(string text)
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var telemetryService = Substitute.For<IErrorTelemetryService>();
        var configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
        configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());

        var parser = new IdeSupportTagParser(logger, telemetryService, configProvider);
        var snapshot = new LspTextSnapshot("file:///workspace/broken.feature", 1, text);
        return parser.Parse(snapshot, ProjectBindingRegistry.Invalid);
    }

    private void SeedMatchService(params StepBindingMatch[] matches) =>
        SeedMatchServiceFor(FeatureUri, matches);

    private void SeedMatchServiceFor(DocumentUri uri, params StepBindingMatch[] matches)
    {
        var matchSet = new FeatureBindingMatchSet(
            uri.ToString(),
            ProjectOwner.Unknown,
            documentVersion: 1,
            registryVersion: 1,
            steps: matches);

        _matchService.Store(matchSet);

        // The handler resolves the step under the request's cursor via the document buffer, so
        // every seeded URI needs a buffer registered too.
        var buffer = new DocumentBuffer(uri, 1, FeatureText);
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = buffer;
                return true;
            });
    }

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block) =>
        UndefinedMatch(text, block, FeatureUri, lineOffset: 0);

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block, int lineOffset) =>
        UndefinedMatch(text, block, FeatureUri, lineOffset);

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block, DocumentUri uri) =>
        UndefinedMatch(text, block, uri, lineOffset: 0);

    private static StepBindingMatch UndefinedMatch(string text, ScenarioBlock block, DocumentUri uri, int lineOffset)
    {
        var keyword = block switch
        {
            ScenarioBlock.Given => "Given ",
            ScenarioBlock.When  => "When ",
            _                   => "Then "
        };
        var gherkinStep = new IdeSupportGherkinStep(
            new Gherkin.Ast.Location(0, 0), keyword, StepKeywordType.Context, text, null!,
            StepKeyword.Given, block);

        var item = MatchResultItem.CreateUndefined(gherkinStep, text);
        var result = MatchResult.CreateMultiMatch(new[] { item });

        var snapshot = new LspTextSnapshot(uri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, lineOffset, text.Length);

        return new StepBindingMatch(uri.ToString(), range, result,
            keyword.Trim(), "S", null);
    }

    /// <summary>Builds an ambiguous <see cref="StepBindingMatch"/> (two conflicting bindings) at the given offset.</summary>
    private static StepBindingMatch AmbiguousMatch(string text, int lineOffset, int length)
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, lineOffset, length);

        var items = new[] { "StepsA.Handle", "StepsB.Handle" }
            .Select(method =>
            {
                var binding = new ProjectStepDefinitionBinding(
                    ScenarioBlock.Given,
                    new Regex($"^{text}$"),
                    null,
                    new ProjectBindingImplementation(method, null, new SourceLocation("Steps.cs", 1, 1)));
                return MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch).CloneToAmbiguousItem();
            })
            .ToArray();

        return new StepBindingMatch(
            FeatureUri.ToString(), range, MatchResult.CreateMultiMatch(items),
            "Given", "S", null);
    }

    /// <summary>Builds a defined <see cref="StepBindingMatch"/> whose binding lives in <paramref name="sourceFile"/> (feeds the append-candidate ranker).</summary>
    private static StepBindingMatch DefinedMatch(string text, ScenarioBlock block, DocumentUri uri, string sourceFile, int lineOffset)
    {
        var binding = new ProjectStepDefinitionBinding(
            block,
            new Regex($"^{Regex.Escape(text)}$"),
            null,
            new ProjectBindingImplementation("Handle", null, new SourceLocation(sourceFile, 1, 1)));

        var item   = MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);
        var result = MatchResult.CreateMultiMatch(new[] { item });

        var snapshot = new LspTextSnapshot(uri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, lineOffset, text.Length);

        var keyword = block switch
        {
            ScenarioBlock.Given => "Given",
            ScenarioBlock.When  => "When",
            _                   => "Then"
        };

        return new StepBindingMatch(uri.ToString(), range, result, keyword, "S", null);
    }
}
