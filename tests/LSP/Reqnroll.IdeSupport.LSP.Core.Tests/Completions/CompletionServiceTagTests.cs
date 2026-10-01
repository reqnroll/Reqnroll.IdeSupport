using NSubstitute;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using Reqnroll.IdeSupport.LSP.Core.Completions.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Completions;

/// <summary>
/// Tests for <see cref="CompletionService.GetTagCompletions"/> — the built-in @ignore plus the
/// project's already-used tags, ranked and filtered (issue #828).
/// </summary>
public class CompletionServiceTagTests
{
    private readonly CompletionService _sut = new();

    private static readonly ReturnAllCompletionMatcher ReturnAll = new();

    [Fact]
    public void Empty_project_always_offers_the_builtin_ignore_tag()
    {
        var result = _sut.GetTagCompletions(Array.Empty<StepCandidate>(), Array.Empty<string>(), "", ReturnAll);

        result.Entries.Select(e => e.Label).Should().Contain("@ignore");
    }

    [Fact]
    public void Project_tags_are_offered_alongside_the_builtin()
    {
        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@smoke", 2), new StepCandidate("@wip", 1) },
            Array.Empty<string>(), "", ReturnAll);

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("@smoke");
        labels.Should().Contain("@wip");
        labels.Should().Contain("@ignore");
    }

    [Fact]
    public void Tags_used_on_the_completing_line_are_excluded()
    {
        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@smoke", 2), new StepCandidate("@wip", 1) },
            new[] { "@smoke" }, "", ReturnAll);

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().NotContain("@smoke");
        labels.Should().Contain("@wip");
    }

    [Fact]
    public void Usage_counts_rank_most_used_tags_first()
    {
        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@rare", 1), new StepCandidate("@hot", 9) },
            Array.Empty<string>(), "", ReturnAll);

        result.Entries.Select(e => e.Label).Should().ContainInOrder("@hot", "@rare", "@ignore");
    }

    [Fact]
    public void A_project_tag_named_like_a_builtin_is_deduplicated_and_keeps_its_usage_count()
    {
        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@ignore", 4) },
            Array.Empty<string>(), "", ReturnAll);

        result.Entries.Where(e => e.Label == "@ignore").Should().ContainSingle()
            .Which.SortText.Should().Be("000000", "@ignore used 4 times ranks above the built-in zero entry");
    }

    [Fact]
    public void Duplicate_project_tags_are_deduplicated_and_counts_summed()
    {
        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@smoke", 2), new StepCandidate("@smoke", 3) },
            Array.Empty<string>(), "", ReturnAll);

        result.Entries.Where(e => e.Label == "@smoke").Should().ContainSingle()
            .Which.SortText.Should().Be("000000", "count 5 makes it the most-used tag");
    }

    [Fact]
    public void Typed_text_narrows_candidates_like_step_samples()
    {
        var matcher = new PrefixMatcher();

        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@smoke", 2), new StepCandidate("@wip", 1) },
            Array.Empty<string>(), "sm", matcher);

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("@smoke");
        labels.Should().NotContain("@wip");
        labels.Should().NotContain("@ignore");
    }

    [Fact]
    public void IsIncomplete_is_propagated_from_the_matcher()
    {
        var matcher = Substitute.For<ICompletionMatcher>();
        matcher.IsIncomplete.Returns(true);
        matcher.Rank(Arg.Any<string>(), Arg.Any<IReadOnlyList<StepCandidate>>())
            .Returns(callInfo => ReturnAll.Rank(
                callInfo.Arg<string>(),
                callInfo.Arg<IReadOnlyList<StepCandidate>>()));

        var result = _sut.GetTagCompletions(
            new[] { new StepCandidate("@smoke", 1) },
            Array.Empty<string>(), "", matcher);

        result.IsIncomplete.Should().BeTrue();
    }

    [Fact]
    public void Builtin_ignore_entry_carries_an_explanatory_detail()
    {
        var result = _sut.GetTagCompletions(Array.Empty<StepCandidate>(), Array.Empty<string>(), "", ReturnAll);

        result.Entries.Should().ContainSingle().Which.Detail.Should().NotBeNullOrEmpty();
    }

    /// <summary>Server-side prefix narrowing, standing in for the FuzzySharp contingency matcher.</summary>
    private sealed class PrefixMatcher : ICompletionMatcher
    {
        public bool IsIncomplete => false;

        public IReadOnlyList<ScoredCandidate> Rank(string typed, IReadOnlyList<StepCandidate> candidates)
            => candidates
                .Where(c => c.Sample.StartsWith("@" + typed, StringComparison.OrdinalIgnoreCase))
                .Select(c => new ScoredCandidate(c.Sample, 0.0))
                .ToList();
    }
}
