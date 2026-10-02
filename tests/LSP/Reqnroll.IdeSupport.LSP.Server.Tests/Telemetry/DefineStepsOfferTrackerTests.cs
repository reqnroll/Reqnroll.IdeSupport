using Gherkin;
using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

/// <summary>Issue #847: offer -> definition correlation behind the StepDefined event.</summary>
public class DefineStepsOfferTrackerTests
{
    private const string FeatureId = "file:///workspace/test.feature";
    private const string NewFile = "C:/proj/StepDefinitions/TestSteps.cs";
    private const string ExistingFile = "C:/proj/StepDefinitions/Other.cs";
    private const string FeatureText =
        "Feature: F\n  Scenario: S\n    Given  I press   add\n    When I press subtract\n    Then the total is 3\n";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly ILspTelemetryService _telemetry = Substitute.For<ILspTelemetryService>();
    private readonly ManualTimeProvider _time = new();

    private DefineStepsOfferTracker CreateSut(TimeSpan? window = null) => new(_telemetry, _time, window);

    private static StepBindingMatch Step(string text, string? definedIn = null, string featureId = FeatureId, string documentText = FeatureText)
    {
        var snapshot = new LspTextSnapshot(featureId, 1, documentText);
        var start = documentText.IndexOf(text, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the fixture text must contain the step");
        var range = GherkinRange.FromPoint(snapshot, start, text.Length);

        MatchResult result;
        if (definedIn is null)
        {
            result = MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateUndefined(
                    new IdeSupportGherkinStep(new Gherkin.Ast.Location(0, 0), "Given ", StepKeywordType.Context, text, null!, StepKeyword.Given, ScenarioBlock.Given),
                    text),
            });
        }
        else
        {
            var binding = new ProjectStepDefinitionBinding(
                ScenarioBlock.Given,
                new Regex($"^{Regex.Escape(text)}$"),
                null,
                new ProjectBindingImplementation("Handle", null, new SourceLocation(definedIn, 1, 1)));
            result = MatchResult.CreateMultiMatch(new[] { MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch) });
        }
        return new StepBindingMatch(featureId, range, result, "Given");
    }

    private static FeatureBindingMatchSet SetOf(string featureId, params StepBindingMatch[] steps) =>
        new(featureId, ProjectOwner.Unknown, 1, 1, steps);

    private static void Offer(DefineStepsOfferTracker sut, params StepBindingMatch[] undefined) =>
        sut.RecordOffer(FeatureId, undefined, SnippetExpressionStyle.CucumberExpression,
            new[] { NewFile }, new[] { ExistingFile });

    private Dictionary<string, object?> SingleSentEvent()
    {
        _telemetry.Received(1).SendEvent(TelemetryEvents.StepDefined, Arg.Any<Dictionary<string, object?>>());
        return (Dictionary<string, object?>)_telemetry.ReceivedCalls().Single().GetArguments()[1]!;
    }

    [Fact]
    public void Step_defined_in_the_offered_new_file_is_reported_as_QuickFixNewFile()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        var props = SingleSentEvent();
        props[TelemetryProperties.Count].Should().Be(1);
        props[TelemetryProperties.Via].Should().Be(TelemetryProperties.StepDefinedVia.QuickFixNewFile);
        props[TelemetryProperties.ExpressionStyle].Should().Be(TelemetryProperties.ExpressionStyles.CucumberExpression);
    }

    [Fact]
    public void Step_defined_in_an_offered_append_candidate_is_reported_as_QuickFixAppend_even_with_backslash_paths()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: ExistingFile.Replace('/', '\\').ToUpperInvariant())));

        SingleSentEvent()[TelemetryProperties.Via].Should().Be(TelemetryProperties.StepDefinedVia.QuickFixAppend);
    }

    [Fact]
    public void Step_defined_somewhere_else_is_reported_as_Other()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: "C:/elsewhere/Mine.cs")));

        SingleSentEvent()[TelemetryProperties.Via].Should().Be(TelemetryProperties.StepDefinedVia.Other);
    }

    [Fact]
    public void Regex_style_is_reported_for_regular_expression_offers()
    {
        var sut = CreateSut();
        sut.RecordOffer(FeatureId, new[] { Step("I press   add") }, SnippetExpressionStyle.AsyncRegularExpression,
            new[] { NewFile }, Array.Empty<string>());

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        SingleSentEvent()[TelemetryProperties.ExpressionStyle].Should().Be(TelemetryProperties.ExpressionStyles.RegularExpression);
    }

    [Fact]
    public void Several_steps_defined_at_once_are_counted_in_one_event_per_Via()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"), Step("I press subtract"), Step("the total is 3"));

        sut.Observe(SetOf(FeatureId,
            Step("I press   add", definedIn: NewFile),
            Step("I press subtract", definedIn: NewFile),
            Step("the total is 3")));

        SingleSentEvent()[TelemetryProperties.Count].Should().Be(2);
    }

    [Fact]
    public void Each_step_is_reported_once_and_the_remainder_later()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"), Step("I press subtract"));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile), Step("I press subtract")));
        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile), Step("I press subtract")));
        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile), Step("I press subtract", definedIn: NewFile)));

        _telemetry.Received(2).SendEvent(TelemetryEvents.StepDefined, Arg.Any<Dictionary<string, object?>>());
        sut.TrackedFeatureCount.Should().Be(0, "an offer with nothing left to observe is dropped");
    }

    [Fact]
    public void No_event_when_no_offer_was_made_for_the_feature()
    {
        var sut = CreateSut();

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        _telemetry.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void No_event_for_a_step_that_was_not_part_of_the_offer()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press subtract", definedIn: NewFile)));

        _telemetry.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void No_event_for_an_offer_made_for_a_different_feature()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));
        const string other = "file:///workspace/other.feature";

        sut.Observe(SetOf(other, Step("I press   add", definedIn: NewFile, featureId: other)));

        _telemetry.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void No_event_while_the_step_is_still_undefined()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press   add")));

        _telemetry.ReceivedCalls().Should().BeEmpty();
        sut.TrackedFeatureCount.Should().Be(1);
    }

    [Fact]
    public void Definition_after_the_window_has_expired_is_not_reported_and_the_offer_is_dropped()
    {
        var sut = CreateSut(window: TimeSpan.FromMinutes(10));
        Offer(sut, Step("I press   add"));

        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        _telemetry.ReceivedCalls().Should().BeEmpty();
        sut.TrackedFeatureCount.Should().Be(0);
    }

    [Fact]
    public void Definition_just_inside_the_window_is_reported()
    {
        var sut = CreateSut(window: TimeSpan.FromMinutes(10));
        Offer(sut, Step("I press   add"));

        _time.Advance(TimeSpan.FromMinutes(9));
        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        SingleSentEvent();
    }

    [Fact]
    public void A_repeated_offer_refreshes_the_window()
    {
        var sut = CreateSut(window: TimeSpan.FromMinutes(10));
        Offer(sut, Step("I press   add"));
        _time.Advance(TimeSpan.FromMinutes(8));
        Offer(sut, Step("I press   add"));
        _time.Advance(TimeSpan.FromMinutes(8));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        SingleSentEvent();
    }

    [Fact]
    public void Expired_offers_are_purged_when_a_new_offer_arrives()
    {
        var sut = CreateSut(window: TimeSpan.FromMinutes(10));
        for (var i = 0; i < 5; i++)
            sut.RecordOffer($"file:///workspace/f{i}.feature", new[] { Step("I press   add") },
                SnippetExpressionStyle.CucumberExpression, new[] { NewFile }, Array.Empty<string>());
        sut.TrackedFeatureCount.Should().Be(5);

        _time.Advance(TimeSpan.FromMinutes(11));
        Offer(sut, Step("I press   add"));

        sut.TrackedFeatureCount.Should().Be(1);
    }

    [Fact]
    public void Number_of_tracked_features_is_bounded_and_the_oldest_offer_is_evicted()
    {
        var sut = CreateSut();
        var total = DefineStepsOfferTracker.MaxTrackedFeatures + 20;
        for (var i = 0; i < total; i++)
        {
            sut.RecordOffer($"file:///workspace/f{i}.feature", new[] { Step("I press   add") },
                SnippetExpressionStyle.CucumberExpression, new[] { NewFile }, Array.Empty<string>());
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        sut.TrackedFeatureCount.Should().Be(DefineStepsOfferTracker.MaxTrackedFeatures);

        // The oldest (f0) was evicted; the newest is still tracked.
        const string oldest = "file:///workspace/f0.feature";
        sut.Observe(SetOf(oldest, Step("I press   add", definedIn: NewFile, featureId: oldest)));
        _telemetry.ReceivedCalls().Should().BeEmpty();

        var newest = $"file:///workspace/f{total - 1}.feature";
        sut.Observe(SetOf(newest, Step("I press   add", definedIn: NewFile, featureId: newest)));
        SingleSentEvent();
    }

    [Fact]
    public void Steps_remembered_per_offer_are_bounded()
    {
        var sut = CreateSut();
        var count = DefineStepsOfferTracker.MaxStepsPerOffer + 50;
        var text = "Feature: F\n  Scenario: S\n"
            + string.Join("\n", Enumerable.Range(0, count).Select(i => $"    Given step number {i:D4}")) + "\n";
        var undefined = Enumerable.Range(0, count).Select(i => Step($"step number {i:D4}", documentText: text)).ToArray();

        Offer(sut, undefined);
        sut.Observe(SetOf(FeatureId,
            Enumerable.Range(0, count).Select(i => Step($"step number {i:D4}", NewFile, documentText: text)).ToArray()));

        SingleSentEvent()[TelemetryProperties.Count].Should().Be(DefineStepsOfferTracker.MaxStepsPerOffer);
    }

    [Fact]
    public void Properties_carry_only_the_closed_set_and_a_count_never_step_text_or_paths()
    {
        var sut = CreateSut();
        Offer(sut, Step("I press   add"));

        sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        var props = SingleSentEvent();
        props.Keys.Should().BeEquivalentTo(new[]
        {
            TelemetryProperties.Count, TelemetryProperties.Via, TelemetryProperties.ExpressionStyle,
        });
    }

    [Fact]
    public void Observe_without_a_telemetry_service_does_not_throw()
    {
        var sut = new DefineStepsOfferTracker(null, _time);
        sut.RecordOffer(FeatureId, new[] { Step("I press   add") }, SnippetExpressionStyle.CucumberExpression,
            new[] { NewFile }, Array.Empty<string>());

        var act = () => sut.Observe(SetOf(FeatureId, Step("I press   add", definedIn: NewFile)));

        act.Should().NotThrow();
    }
}
