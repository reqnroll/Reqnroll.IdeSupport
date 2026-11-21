using AwesomeAssertions;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Steps for the rate limit on server output while a document is being edited: a burst of edits must collapse
/// into a bounded number of <c>publishDiagnostics</c> notifications for clients that ask for it, and the last edit's
/// diagnostics must still arrive. Both halves matter: too many publishes is the per-keystroke storm, too few (or one
/// that never reflects the last edit) is a stale squiggle.
/// </summary>
[Binding]
public sealed class OutputThrottlingSteps
{
    private readonly LspScenarioContext _ctx;

    // Filler scenarios appended to every revision, so a document can be made large enough to be throttled.
    private string _padding = string.Empty;

    public OutputThrottlingSteps(LspScenarioContext ctx) => _ctx = ctx;

    /// <summary>
    /// Opens a valid document of about <paramref name="lines"/> lines. Every later revision in the scenario carries
    /// the same filler, so the document stays that size while it is edited.
    /// </summary>
    [When(@"the feature file ""([^""]*)"" is opened as a document of (\d+) lines")]
    public async Task WhenTheFeatureFileIsOpenedAsADocumentOfNLines(string fileName, int lines)
    {
        _padding = Padding(lines);

        await _ctx.EnsureStartedAsync();
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;
        _ctx.LastDocumentText = ValidRevision(0) + _padding;
        _ctx.LastVersion = 1;
        _ctx.Harness.Client.OpenDocument(uri, 1, _ctx.LastDocumentText);
        _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensAsync(uri);
    }

    /// <summary>
    /// Sends <paramref name="count"/> didChange notifications back to back, the way a client does while someone is
    /// typing. The last revision has a Gherkin parse error and the revisions before it alternate between valid and
    /// broken, so every edit changes the diagnostics (an unchanged set is not published again), and a lost final
    /// publish would leave the valid revision before it as the last state the client saw.
    /// </summary>
    [When(@"the feature file ""([^""]*)"" is edited (\d+) times in rapid succession, the last edit introducing a parse error")]
    public void WhenTheFeatureFileIsEditedRapidlyEndingInAParseError(string fileName, int count)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;

        for (var i = 1; i <= count; i++)
        {
            // Alternate between a valid and a broken revision, ending on a broken one, so that every edit changes
            // the document's diagnostics: the server does not publish a set the client already holds.
            var broken = (count - i) % 2 == 0;
            _ctx.LastDocumentText = (broken ? BrokenRevision(i) : ValidRevision(i)) + _padding;
            _ctx.LastVersion += 1;
            _ctx.Harness.Client.ChangeDocument(uri, _ctx.LastVersion, _ctx.LastDocumentText);
        }
    }

    [Then(@"at most (\d+) diagnostics publishes are sent for ""([^""]*)""")]
    public async Task ThenAtMostNDiagnosticsPublishesAreSent(int max, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        await _ctx.Harness.WaitForDiagnosticsQuiescenceAsync().ConfigureAwait(false);

        _ctx.Harness.PublishedDiagnosticsCountFor(uri).Should().BeLessThanOrEqualTo(max,
            "a burst of edits should collapse into a bounded number of publishDiagnostics notifications " +
            "rather than one per keystroke");
    }

    [Then(@"at least (\d+) diagnostics publishes are sent for ""([^""]*)""")]
    public async Task ThenAtLeastNDiagnosticsPublishesAreSent(int min, string fileName)
    {
        var uri = _ctx.UriFor(fileName);
        await _ctx.Harness.WaitForDiagnosticsQuiescenceAsync().ConfigureAwait(false);

        _ctx.Harness.PublishedDiagnosticsCountFor(uri).Should().BeGreaterThanOrEqualTo(min,
            "a client that did not ask for throttling should still get a publish for every change");
    }

    // Three lines per filler scenario: a blank line, the scenario and its step.
    private static string Padding(int lines)
    {
        var text = new System.Text.StringBuilder();
        for (var k = 0; k < lines / 3; k++)
            text.Append("\n\nScenario: Filler ").Append(k).Append("\n\tWhen I press add");
        return text.ToString();
    }

    private static string ValidRevision(int i) =>
        $"""
         Feature: Throttle

         Scenario: Edit {i}
         	When I press add
         """;

    private static string BrokenRevision(int i) =>
        $"""
         Feature: Throttle

         Scenario: Edit {i}
         	When I press add
         	| unattached | table |
         Nonsense line that is not Gherkin
         """;
}
