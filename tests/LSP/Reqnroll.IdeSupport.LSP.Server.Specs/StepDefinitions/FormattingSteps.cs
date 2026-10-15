using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class FormattingSteps
{
    private readonly LspScenarioContext _ctx;

    public FormattingSteps(LspScenarioContext ctx) => _ctx = ctx;

    [When(@"the document ""(.*)"" is formatted")]
    public async Task WhenTheDocumentIsFormatted(string fileName)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFormattingEdits = await _ctx.Harness.Client
            .RequestFormattingAsync(uri)
            .ConfigureAwait(false);
    }

    [When(@"the document ""(.*)"" is formatted and an error is expected")]
    public async Task WhenTheDocumentIsFormattedAndAnErrorIsExpected(string fileName)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFormattingEdits = null;
        _ctx.LastFormattingError = null;
        try
        {
            _ctx.LastFormattingEdits = await _ctx.Harness.Client
                .RequestFormattingAsync(uri)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ctx.LastFormattingError = ex;
        }
    }

    [When(@"range formatting is requested for ""(.*)"" from line (\d+) to line (\d+)")]
    public async Task WhenRangeFormattingIsRequested(string fileName, int startLine, int endLine)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFormattingEdits = await _ctx.Harness.Client
            .RequestRangeFormattingAsync(uri, startLine, endLine)
            .ConfigureAwait(false);
    }

    [When(@"on-type formatting is requested for ""(.*)"" at line (\d+) column (\d+) with trigger ""(.*)""")]
    public async Task WhenOnTypeFormattingIsRequested(string fileName, int line, int column, string trigger)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var uri = _ctx.UriFor(fileName);
        _ctx.LastFormattingEdits = await _ctx.Harness.Client
            .RequestOnTypeFormattingAsync(uri, line, column, trigger)
            .ConfigureAwait(false);
    }

    [Then("formatting edits are returned")]
    public void ThenFormattingEditsAreReturned()
    {
        _ctx.LastFormattingEdits.Should().NotBeNull("the server should return formatting edits for a .feature file");
        _ctx.LastFormattingEdits!.Should().NotBeEmpty("at least one edit should be produced");
    }

    [Then("no formatting edits are returned")]
    public void ThenNoFormattingEditsAreReturned()
    {
        var count = _ctx.LastFormattingEdits?.Length ?? 0;
        count.Should().Be(0, "non-.feature files should not receive formatting edits");
    }

    [Then(@"formatting fails with error message ""(.*)""")]
    public void ThenFormattingFailsWithErrorMessage(string expectedMessage)
    {
        _ctx.LastFormattingEdits.Should().BeNull("a rejected format must not also return edits");
        _ctx.LastFormattingError.Should().NotBeNull(
            "a document that cannot be formatted must surface as a JSON-RPC error response (issue #1132), not a silent empty result");
        _ctx.LastFormattingError!.Message.Should().Contain(expectedMessage);
    }

    [Then(@"the formatted text contains ""(.*)""")]
    public void ThenTheFormattedTextContains(string expected)
    {
        _ctx.LastFormattingEdits.Should().NotBeNull();
        var newText = _ctx.LastFormattingEdits![0].NewText;
        newText.Should().Contain(expected, $"the formatted document should contain the line '{expected}'");
    }
}
