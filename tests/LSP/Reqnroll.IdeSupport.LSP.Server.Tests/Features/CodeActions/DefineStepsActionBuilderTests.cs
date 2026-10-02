using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeActions;

/// <summary>
/// Direct unit tests for <see cref="DefineStepsActionBuilder"/> (issue #588), extracted from
/// <see cref="CodeActionHandler.Handle"/>'s nested <c>BuildTargetedActions</c> local function so
/// "given this target and these steps, build the actions" is testable in isolation from request
/// handling, guards, and telemetry.
/// </summary>
public class DefineStepsActionBuilderTests : IDisposable
{
    private readonly IStepScaffoldService _scaffoldService = new StepScaffoldService();
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly ICSharpFileTextCache _csharpFileTextCache = new CSharpFileTextCache();
    private readonly string _projectFolder;

    private const string FeatureText = "Feature: F\nScenario: S\n    When I press add\n";
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public DefineStepsActionBuilderTests()
    {
        _projectFolder = Path.Combine(Path.GetTempPath(), "DSABTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectFolder);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectFolder)) Directory.Delete(_projectFolder, recursive: true); } catch { /* best-effort */ }
    }

    private DefineStepsActionBuilder CreateSut() => new(_scaffoldService, _fileSystem, _csharpFileTextCache);

    private static StepDefinitionTarget MakeTarget(string targetPath, params string[] appendCandidates) =>
        new(
            SnippetExpressionStyle.CucumberExpression,
            new CSharpCodeGenerationConfiguration(),
            ClassName: Path.GetFileNameWithoutExtension(targetPath),
            Namespace: "MyProject",
            TargetPath: targetPath,
            AppendCandidates: appendCandidates,
            Indent: "    ",
            NewLine: "\n");

    private static StepBindingMatch UndefinedMatch(string text)
    {
        var gherkinStep = new IdeSupportGherkinStep(
            new Gherkin.Ast.Location(0, 0), "When ", StepKeywordType.Action, text, null!,
            StepKeyword.When, ScenarioBlock.When);

        var item = MatchResultItem.CreateUndefined(gherkinStep, text);
        var result = MatchResult.CreateMultiMatch(new[] { item });

        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, FeatureText);
        var range    = GherkinRange.FromPoint(snapshot, 0, text.Length);

        return new StepBindingMatch(FeatureUri.ToString(), range, result, "When", "S", null);
    }

    [Fact]
    public void Build_returns_a_single_unadorned_new_file_action_when_there_are_no_append_candidates()
    {
        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"));

        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch("I press add") });

        actions.Should().ContainSingle();
        var action = actions[0].CodeAction!;
        action.Title.Should().Be("Define missing step");
        action.IsPreferred.Should().BeTrue();
    }

    [Fact]
    public void Build_returns_no_actions_when_the_scaffolder_produces_no_snippets()
    {
        // No steps at all -- BuildDescriptors returns an empty set, so RenderSnippets returns
        // null and the method must bail before touching the filesystem at all.
        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"));

        var actions = CreateSut().Build(target, "Define missing step", Array.Empty<StepBindingMatch>());

        actions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_returns_no_actions_when_the_only_undefined_step_has_blank_text(string blankText)
    {
        // A bare keyword with no step text (issue #622) has nothing to build a skeleton from --
        // BuildDescriptors filters it out, so this must behave the same as no steps at all.
        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"));

        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch(blankText) });

        actions.Should().BeEmpty();
    }

    [Fact]
    public void Build_suffixes_titles_and_marks_only_the_append_action_preferred_when_a_candidate_succeeds()
    {
        var candidatePath = Path.Combine(_projectFolder, "ExistingSteps.cs");
        File.WriteAllText(candidatePath,
            "namespace MyProject;\n\n[Binding]\npublic class ExistingSteps\n{\n}\n");
        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"), candidatePath);

        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch("I press add") });

        actions.Should().HaveCount(2);
        var titles = actions.Select(a => a.CodeAction!.Title).ToList();
        titles.Should().Contain("Define missing step → ExistingSteps.cs");
        titles.Should().Contain("Define missing step → new file");

        // Exactly one action per group is preferred -- the append candidate, since it's ranked
        // first -- not zero, not both.
        actions.Count(a => a.CodeAction!.IsPreferred == true).Should().Be(1);
        actions.Single(a => a.CodeAction!.Title.EndsWith("ExistingSteps.cs"))
               .CodeAction!.IsPreferred.Should().BeTrue();
        actions.Single(a => a.CodeAction!.Title.EndsWith("new file"))
               .CodeAction!.IsPreferred.Should().BeFalse();
    }

    [Fact]
    public void Build_falls_back_to_the_unadorned_title_when_the_only_candidate_cannot_be_appended_to()
    {
        // A candidate with no locatable class body (AppendToFile returns null) must not count
        // toward "multiTarget" -- the new-file action should keep its plain title, since there is
        // in fact no real choice being offered to the user.
        var candidatePath = Path.Combine(_projectFolder, "Unparseable.cs");
        File.WriteAllText(candidatePath, "not valid C# at all {{{");
        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"), candidatePath);

        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch("I press add") });

        actions.Should().ContainSingle();
        actions[0].CodeAction!.Title.Should().Be("Define missing step");
        actions[0].CodeAction!.IsPreferred.Should().BeTrue();
    }

    [Fact]
    public void Build_appends_onto_the_candidate_s_live_cached_text_rather_than_stale_disk_content()
    {
        // Reproduces issue #815: an earlier "Define step" action already appended WhenFoo to this
        // candidate via workspace/applyEdit. That edit landed only in the client's open, unsaved
        // editor buffer -- disk still has the pre-edit (no-WhenFoo) content, exactly as it would
        // between two lightbulb invocations with nothing saved in between.
        var candidatePath = Path.Combine(_projectFolder, "ExistingSteps.cs");
        File.WriteAllText(candidatePath,
            "namespace MyProject;\n\n[Binding]\npublic class ExistingSteps\n{\n}\n");

        var liveContent =
            "namespace MyProject;\n\n[Binding]\npublic class ExistingSteps\n{\n" +
            "    [When(\"foo\")]\n    public void WhenFoo()\n    {\n        throw new PendingStepException();\n    }\n}\n";
        _csharpFileTextCache.Update(DocumentUri.FromFileSystemPath(candidatePath), liveContent);

        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"), candidatePath);
        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch("I press bar") });

        var newText = AppendedNewText(actions, "ExistingSteps.cs");

        // Both methods must be present exactly once each, inside one class -- not the doubled
        // class/closing-brace corruption #815 reports when the append is computed from stale
        // disk content instead of the live buffer the second edit will actually land on top of.
        CountOccurrences(newText, "public class ExistingSteps").Should().Be(1);
        CountOccurrences(newText, "WhenFoo").Should().Be(1);
        CountOccurrences(newText, "I press bar").Should().Be(1);
        CountOccurrences(newText, "PendingStepException").Should().Be(2);
    }

    [Fact]
    public void Build_falls_back_to_disk_for_a_candidate_the_cache_has_never_seen()
    {
        // A candidate discovered purely via the binding registry (e.g. connector reflection over
        // the built assembly) and never opened this session has no cache entry -- disk remains
        // the only, and correct, source for it.
        var candidatePath = Path.Combine(_projectFolder, "ExistingSteps.cs");
        File.WriteAllText(candidatePath,
            "namespace MyProject;\n\n[Binding]\npublic class ExistingSteps\n{\n}\n");

        var target = MakeTarget(Path.Combine(_projectFolder, "MySteps.cs"), candidatePath);
        var actions = CreateSut().Build(target, "Define missing step", new[] { UndefinedMatch("I press add") });

        var newText = AppendedNewText(actions, "ExistingSteps.cs");
        newText.Should().Contain("I press add");
    }

    private static string AppendedNewText(List<CommandOrCodeAction> actions, string candidateFileName)
    {
        var appendAction = actions.Single(a => a.CodeAction!.Title.EndsWith(candidateFileName)).CodeAction!;
        var edit = appendAction.Edit!.DocumentChanges!.First(c => c.IsTextDocumentEdit).TextDocumentEdit!;
        return edit.Edits.First().NewText;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
