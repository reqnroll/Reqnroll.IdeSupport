using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class CommentToggleSteps
{
    private readonly LspScenarioContext _ctx;

    public CommentToggleSteps(LspScenarioContext ctx) => _ctx = ctx;

    // -- When ------------------------------------------------------------------

    [When("the toggle comment command is executed for \"(.*)\" on lines (\\d+) to (\\d+)")]
    public Task WhenTheToggleCommentCommandIsExecuted(string fileName, int startLine, int endLine)
        => ExecuteToggleCommentAsync(fileName, new JArray(startLine, endLine));

    [When("the toggle comment command is executed for \"(.*)\" on lines (\\d+) to (\\d+) with mode \"(.*)\"")]
    public Task WhenTheToggleCommentCommandIsExecutedWithMode(string fileName, int startLine, int endLine, string mode)
        => ExecuteToggleCommentAsync(fileName, new JArray(startLine, endLine, mode));

    private async Task ExecuteToggleCommentAsync(string fileName, JArray argumentsAfterUri)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.LastToggleEdit = null;

        var arguments = new JArray(uri.ToString());
        foreach (var argument in argumentsAfterUri)
            arguments.Add(argument);

        await _ctx.Harness.Client.RequestCommandAsync(new ExecuteCommandParams
        {
            Command = "reqnroll.toggleComment",
            Arguments = arguments
        }).ConfigureAwait(false);

        _ctx.LastToggleEdit = _ctx.Harness.LastApplyEdit;
    }

    // -- Then ------------------------------------------------------------------

    [Then("a workspace\\/applyEdit request is received")]
    public void ThenAWorkspaceApplyEditRequestIsReceived()
    {
        _ctx.LastToggleEdit.Should().NotBeNull("the server should send workspace/applyEdit as a request");
    }

    [Then("the edit does not change line (\\d+)")]
    public void ThenTheEditDoesNotChangeLine(int line)
    {
        var edit = _ctx.LastToggleEdit;
        edit.Should().NotBeNull();

        var docEdit = edit!.Edit.DocumentChanges!.First().TextDocumentEdit;
        docEdit.Should().NotBeNull("the edit should contain a TextDocumentEdit");
        docEdit!.Edits.Should().NotContain(e => e.Range.Start.Line == line,
            $"line {line} should be left untouched");
    }

    [Then("the edit replaces line (\\d+) with \"(.*)\"")]
    public void ThenTheEditReplacesLineWith(int line, string expectedText)
    {
        var edit = _ctx.LastToggleEdit;
        edit.Should().NotBeNull();

        var docChanges = edit!.Edit.DocumentChanges;
        docChanges.Should().NotBeNull();
        var docEdit = docChanges!.First().TextDocumentEdit;
        docEdit.Should().NotBeNull("the edit should contain a TextDocumentEdit");

        var textEdit = docEdit!.Edits.Should().ContainSingle(
            e => e.Range.Start.Line == line && e.Range.End.Line == line,
            $"a text edit for line {line} should exist").Subject;
        textEdit.NewText.Should().Be(expectedText);
        textEdit.Range.End.Character.Should().BeGreaterThan(0,
            "the edit must replace the line content, not insert at column 0 (which would duplicate the existing text)");
    }
}
