using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class CompletionSteps
{
    private readonly LspScenarioContext _ctx;

    public CompletionSteps(LspScenarioContext ctx) => _ctx = ctx;

    // Opens a document without waiting for semantic tokens — used for files the server ignores.
    [Given(@"the file ""(.*)"" is open with content")]
    public async Task GivenTheFileIsOpenWithContent(string fileName, string content)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.Harness.Client.OpenDocument(uri, 1, content);
    }

    [When(@"completions are requested at line (\d+) column (\d+) in ""(.*)""")]
    public async Task WhenCompletionsAreRequestedAt(int line, int column, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastCompletions = await _ctx.Harness.Client
            .RequestCompletionAsync(uri, line, column)
            .ConfigureAwait(false);
    }

    [Then(@"completions are returned")]
    public void ThenCompletionsAreReturned()
    {
        _ctx.LastCompletions.Should().NotBeNull("the server should return a CompletionList");
        _ctx.LastCompletions!.Items.Should().NotBeEmpty("at least one completion item should be present");
    }

    [Then(@"no completions are returned")]
    public void ThenNoCompletionsAreReturned()
    {
        var count = _ctx.LastCompletions?.Items?.Count() ?? 0;
        count.Should().Be(0, "a non-.feature file should yield no completion items");
    }

    [Then(@"the completions include a keyword label ""(.*)""")]
    public void ThenCompletionsIncludeKeywordLabel(string label)
    {
        _ctx.LastCompletions.Should().NotBeNull();
        _ctx.LastCompletions!.Items.Should().Contain(
            item => item.Label == label,
            $"a keyword completion with label '{label}' should be present");
    }

    [Then(@"the completions include a step label ""(.*)""")]
    public void ThenCompletionsIncludeStepLabel(string label)
    {
        _ctx.LastCompletions.Should().NotBeNull();
        _ctx.LastCompletions!.Items.Should().Contain(
            item => item.Label == label,
            $"a step completion with label '{label}' should be present");
    }

    [Then(@"the completions do not include a label ""(.*)""")]
    public void ThenCompletionsDoNotIncludeLabel(string label)
    {
        if (_ctx.LastCompletions is null) return;
        _ctx.LastCompletions.Items.Should().NotContain(
            item => item.Label == label,
            $"a completion with label '{label}' should not be present");
    }

    // Issue #561: a keyword completion's textEdit used to replace the whole trimmed line, so
    // accepting it deleted any text the user had already typed to the right of the caret (e.g. a
    // scenario title). Every returned item's replacement range must stop at the requested column.
    [Then(@"every completion's textEdit range does not extend past column (\d+)")]
    public void ThenEveryCompletionsTextEditRangeDoesNotExtendPastColumn(int column)
    {
        _ctx.LastCompletions.Should().NotBeNull();
        foreach (var item in _ctx.LastCompletions!.Items)
        {
            item.TextEdit.Should().NotBeNull($"completion '{item.Label}' should carry a textEdit");
            item.TextEdit!.TextEdit.Should().NotBeNull($"completion '{item.Label}' should carry a plain TextEdit");
            item.TextEdit!.TextEdit!.Range.End.Character.Should().BeLessThanOrEqualTo(
                column, $"completion '{item.Label}' must not replace text to the right of the caret");
        }
    }
}
