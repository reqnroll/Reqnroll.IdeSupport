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

    public OutputThrottlingSteps(LspScenarioContext ctx) => _ctx = ctx;

    /// <summary>
    /// Sends <paramref name="count"/> didChange notifications back to back, the way a client does while someone is
    /// typing. The last revision has a Gherkin parse error and every earlier one is valid, so the diagnostics of the
    /// final revision can be told apart from those of any earlier one.
    /// </summary>
    [When(@"the feature file ""([^""]*)"" is edited (\d+) times in rapid succession, the last edit introducing a parse error")]
    public void WhenTheFeatureFileIsEditedRapidlyEndingInAParseError(string fileName, int count)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;

        for (var i = 1; i <= count; i++)
        {
            _ctx.LastDocumentText = i < count ? ValidRevision(i) : BrokenRevision(i);
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
