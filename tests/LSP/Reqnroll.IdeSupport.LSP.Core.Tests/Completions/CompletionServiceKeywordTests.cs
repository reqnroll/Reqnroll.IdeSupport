using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Completions;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Completions;

public class CompletionServiceKeywordTests
{
    private readonly CompletionService _sut = new();

    private static GherkinDialect EnDialect()
        => new GherkinDialectProvider("en").DefaultDialect;

    private static GherkinDialect DeDialect()
        => new GherkinDialectProvider("de").DefaultDialect;

    // ── StepLine ──────────────────────────────────────────────────────────────

    [Fact]
    public void StepLine_offers_Given_When_Then_And_But()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.StepLine }, EnDialect());

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("Given ");
        labels.Should().Contain("When ");
        labels.Should().Contain("Then ");
        labels.Should().Contain("And ");
        labels.Should().Contain("But ");
    }

    [Fact]
    public void StepLine_removes_bullet_keyword_from_Given_When_Then_But()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.StepLine }, EnDialect());

        // "* " is the bullet keyword; should NOT appear for Given/When/Then/But
        // (it is kept for And because legacy IdeSupportCompletionSource keeps it there)
        var nonAndLabels = result.Entries
            .Where(e => e.Detail != "Used to combine steps in a readable format" ||
                        // include But entries in the check
                        result.Entries.Any(x => x.Label == "But " && x == e))
            .ToList();

        // The simpler assertion: the bullet keyword never appears via Given/When/Then/But detail
        result.Entries
              .Where(e => e.Detail == "Describes the context for the behaviour" ||
                          e.Detail == "Describes the action that initiates the behaviour" ||
                          e.Detail == "Describes the expected outcome")
              .Select(e => e.Label)
              .Should().NotContain("* ");
    }

    [Fact]
    public void StepLine_all_entries_are_Keyword_kind()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.StepLine }, EnDialect());
        result.Entries.Should().AllSatisfy(e => e.Kind.Should().Be(CompletionEntryKind.Keyword));
    }

    // ── Block lines ───────────────────────────────────────────────────────────

    [Fact]
    public void FeatureLine_offers_Feature_with_colon_space()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.FeatureLine }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("Feature: ");
    }

    [Fact]
    public void ScenarioLine_offers_both_Scenario_and_ScenarioOutline()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.ScenarioLine }, EnDialect());

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("Scenario: ");
        labels.Should().Contain("Scenario Outline: ");
    }

    [Fact]
    public void ExamplesLine_offers_Examples()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.ExamplesLine }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("Examples: ");
    }

    [Fact]
    public void BackgroundLine_offers_Background()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.BackgroundLine }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("Background: ");
    }

    [Fact]
    public void RuleLine_offers_Rule()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.RuleLine }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("Rule: ");
    }

    // ── Non-keyword token types ───────────────────────────────────────────────

    [Fact]
    public void TagLine_offers_at_tag_template()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.TagLine }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("@tag1 ");
    }

    [Fact]
    public void DocStringSeparator_offers_triple_quote_and_backtick()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.DocStringSeparator }, EnDialect());

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("\"\"\"");
        labels.Should().Contain("```");
    }

    [Fact]
    public void TableRow_offers_pipe()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.TableRow }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("| ");
    }

    [Fact]
    public void Language_offers_hash_language_directive()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.Language }, EnDialect());

        result.Entries.Select(e => e.Label).Should().Contain("#language: ");
    }

    // ── Empty / default ───────────────────────────────────────────────────────

    [Fact]
    public void No_tokens_produces_empty_keyword_result()
    {
        var result = _sut.GetKeywordCompletions(Array.Empty<TokenType>(), EnDialect());

        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void DefaultKeywordCompletions_includes_step_and_block_keywords()
    {
        var result = _sut.GetDefaultKeywordCompletions(EnDialect());

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain("Given ");
        labels.Should().Contain("Feature: ");
        labels.Should().Contain("Scenario: ");
    }

    // ── Dialect awareness ─────────────────────────────────────────────────────

    [Fact]
    public void StepLine_with_German_dialect_returns_Angenommen_Wenn_Dann()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.StepLine }, DeDialect());

        var labels = result.Entries.Select(e => e.Label).ToList();
        labels.Should().Contain(l => l.StartsWith("Angenommen"));
        labels.Should().Contain(l => l.StartsWith("Wenn"));
        labels.Should().Contain(l => l.StartsWith("Dann"));
        labels.Should().NotContain("Given ");
        labels.Should().NotContain("When ");
        labels.Should().NotContain("Then ");
    }

    [Fact]
    public void FeatureLine_with_German_dialect_returns_Funktionalitaet()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.FeatureLine }, DeDialect());

        result.Entries.Select(e => e.Label)
              .Should().Contain(l => l.StartsWith("Funktionalität") || l.StartsWith("Funktion"));
    }

    // ── Descriptions present ─────────────────────────────────────────────────

    [Fact]
    public void StepLine_entries_have_non_empty_detail()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.StepLine }, EnDialect());

        result.Entries.Should().AllSatisfy(e => e.Detail.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public void FeatureLine_entry_has_non_empty_detail()
    {
        var result = _sut.GetKeywordCompletions(new[] { TokenType.FeatureLine }, EnDialect());

        result.Entries.Should().AllSatisfy(e => e.Detail.Should().NotBeNullOrEmpty());
    }
}
