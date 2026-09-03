using AwesomeAssertions;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Steps for the refresh choreography's coalescing behaviour: a burst of edits must collapse into
/// a bounded number of server-initiated <c>workspace/semanticTokens/refresh</c> requests, and the
/// document must still end up reflecting the last edit.
/// <para>
/// Both halves matter and fail in opposite directions. Too many refreshes is the performance shape
/// behind issue #491 — every keystroke costing the client a full re-encode of every open document.
/// Too few, or one that lands before the final edit, is a stale-highlighting bug.
/// </para>
/// </summary>
[Binding]
public sealed class RefreshDebounceSteps
{
    private readonly LspScenarioContext _ctx;

    public RefreshDebounceSteps(LspScenarioContext ctx) => _ctx = ctx;

    /// <summary>
    /// Sends <paramref name="count"/> didChange notifications back to back with no delay, the way
    /// a client does while someone is typing. Each revision names its own scenario, so the final
    /// document content is identifiable and the last edit can be told apart from any earlier one.
    /// </summary>
    [When(@"the feature file ""([^""]*)"" is edited (\d+) times in rapid succession")]
    public void WhenTheFeatureFileIsEditedRapidly(string fileName, int count)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;

        for (var i = 1; i <= count; i++)
        {
            _ctx.LastDocumentText = RevisionText(i);
            _ctx.LastVersion += 1;
            _ctx.Harness.Client.ChangeDocument(uri, _ctx.LastVersion, _ctx.LastDocumentText);
        }
    }

    [Then(@"at most (\d+) semantic tokens refresh requests are sent")]
    public async Task ThenAtMostNRefreshesAreSent(int max)
    {
        // The refresh is debounced, so the count only means anything once the stream has settled.
        await _ctx.Harness.WaitForRefreshQuiescenceAsync().ConfigureAwait(false);

        _ctx.Harness.RefreshCount.Should().BeLessThanOrEqualTo(max,
            "a burst of edits should coalesce into a bounded number of " +
            "workspace/semanticTokens/refresh requests rather than one per keystroke");
    }

    [Then(@"at least (\d+) semantic tokens refresh request is sent")]
    public async Task ThenAtLeastNRefreshesAreSent(int min)
    {
        var reached = await _ctx.Harness.WaitForRefreshAsync(min).ConfigureAwait(false);
        reached.Should().BeTrue(
            $"the client should be asked to refresh at least {min} time(s) after the edits, " +
            $"but only {_ctx.Harness.RefreshCount} arrived");
    }

    /// <summary>The document text for revision <paramref name="i"/> of the burst.</summary>
    private static string RevisionText(int i) =>
        $"""
         Feature: Debounce

         Scenario: Edit {i}
         	When I press add
         """;
}
