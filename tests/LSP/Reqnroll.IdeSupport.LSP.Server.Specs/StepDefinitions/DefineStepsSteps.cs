using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class DefineStepsSteps
{
    private readonly LspScenarioContext _ctx;

    public DefineStepsSteps(LspScenarioContext ctx) => _ctx = ctx;

    // ── When ───────────────────────────────────────────────────────────────────

    [When(@"code actions are requested for ""(.*)"" at line (\d+)")]
    public async Task WhenCodeActionsAreRequestedAt(string fileName, int line)
    {
        var uri   = _ctx.UriFor(fileName);
        var range = new LspRange(new Position(line, 0), new Position(line, 200));
        _ctx.LastCodeActions = await _ctx.Harness.Client
            .RequestCodeActionsAsync(uri, range)
            .ConfigureAwait(false);
    }

    // ── Then ───────────────────────────────────────────────────────────────────

    [Then(@"a code action titled ""(.*)"" is available")]
    public void ThenACodeActionTitledIsAvailable(string title)
    {
        _ctx.LastCodeActions.Should().NotBeNull("code actions should have been returned");
        _ctx.LastCodeActions!.Should().Contain(
            ca => ca.IsCodeAction && ca.CodeAction!.Title == title,
            $"a code action titled '{title}' should be present. Got: " +
            string.Join(", ", (_ctx.LastCodeActions ?? [])
                .Where(ca => ca.IsCodeAction)
                .Select(ca => $"'{ca.CodeAction!.Title}'")));
    }

    [Then(@"no code action titled ""(.*)"" is available")]
    public void ThenNoCodeActionTitledIsAvailable(string title)
    {
        (_ctx.LastCodeActions ?? []).Should().NotContain(
            ca => ca.IsCodeAction && ca.CodeAction!.Title == title,
            $"a code action titled '{title}' should not be offered when there is nothing to define");
    }

    [Then("the code action edit creates a new C# file")]
    public void ThenTheCodeActionEditCreatesANewCSharpFile()
    {
        var action = FirstCodeAction();
        action.Edit.Should().NotBeNull("the code action should carry an inline workspace edit");
        action.Edit!.DocumentChanges.Should().NotBeNull("the edit should use DocumentChanges");
        action.Edit.DocumentChanges!.Should().Contain(
            d => d.CreateFile != null,
            "the workspace edit should contain a CreateFile operation for the new .cs file");
    }

    [Then("the code action edit inserts step definition C# code")]
    public void ThenTheCodeActionEditInsertsStepDefinitionCode()
    {
        var action = FirstCodeAction();
        var textEditChange = action.Edit!.DocumentChanges!
            .FirstOrDefault(d => d.TextDocumentEdit != null);

        textEditChange.TextDocumentEdit.Should().NotBeNull(
            "the workspace edit should include a TextDocumentEdit");

        var content = textEditChange.TextDocumentEdit!.Edits
            .Should().NotBeEmpty("there should be at least one text edit")
            .And.Subject.First().NewText;

        content.Should().Contain("[Binding]",
            "the generated file should include the [Binding] attribute");
        content.Should().Contain("public class",
            "the generated file should contain a class declaration");
    }

    [Then(@"the code action titled ""(.*)"" appends to the existing C# file")]
    public void ThenTheCodeActionTitledAppendsToTheExistingFile(string title)
    {
        _ctx.LastCodeActions.Should().NotBeNull("code actions should have been requested first");
        var match = _ctx.LastCodeActions!.FirstOrDefault(ca => ca.IsCodeAction && ca.CodeAction!.Title == title);
        match.Should().NotBeNull($"a code action titled '{title}' should be present");
        var action = match!.CodeAction!;

        action.Edit.Should().NotBeNull("the code action should carry an inline workspace edit");
        action.Edit!.DocumentChanges.Should().NotContain(
            d => d.CreateFile != null,
            "an append action must not create a new file — it replaces the existing one's content");

        var textEditChange = action.Edit.DocumentChanges!.FirstOrDefault(d => d.TextDocumentEdit != null);
        textEditChange.TextDocumentEdit.Should().NotBeNull("the workspace edit should include a TextDocumentEdit");

        var content = textEditChange.TextDocumentEdit!.Edits
            .Should().NotBeEmpty("there should be at least one text edit")
            .And.Subject.First().NewText;

        content.Should().Contain("WhenTheFirstStepIsBound",
            "the existing step definition method must be preserved, not overwritten");
        content.Should().Contain("[Binding]");
    }

    [Then(@"the code action has a ""(.*)"" command to open the new file")]
    public void ThenTheCodeActionHasACommand(string commandName)
    {
        var action = FirstCodeAction();
        action.Command.Should().NotBeNull(
            "the code action should include a Command so editors can open the newly created file");
        action.Command!.Name.Should().Be(commandName,
            $"the command should be '{commandName}' so VS Code opens the file after applying the edit");
        action.Command.Arguments.Should().NotBeNullOrEmpty(
            "the command should pass the new file's URI as an argument");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private CodeAction FirstCodeAction()
    {
        _ctx.LastCodeActions.Should().NotBeNull("code actions should have been requested first");
        var first = _ctx.LastCodeActions!.FirstOrDefault(ca => ca.IsCodeAction);
        first.Should().NotBeNull("at least one CodeAction (not Command) should be present");
        return first!.CodeAction!;
    }
}
