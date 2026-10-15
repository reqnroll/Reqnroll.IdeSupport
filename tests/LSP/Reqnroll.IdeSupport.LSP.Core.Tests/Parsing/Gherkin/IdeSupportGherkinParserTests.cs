
namespace Reqnroll.IdeSupport.LSP.Core.Tests.Parsing.Gherkin;

public class IdeSupportGherkinParserTests
{
    // The parser must advertise TagLine on a blank / whitespace-only / tag-only line sitting above
    // a Feature, Rule or Scenario line: tag completion (issue #828) relies on it, so a missing
    // popup there is a client-trigger problem, never a parser-state one.
    [Theory]
    [InlineData("\nFeature: F\n", 0)]
    [InlineData(" \nFeature: F\n", 0)]
    [InlineData("@smoke \nFeature: F\n", 0)]
    [InlineData("Feature: F\n\n \nScenario: S\n  Given x\n", 2)]
    [InlineData("Feature: F\n\n@a \nScenario: S\n  Given x\n", 2)]
    [InlineData("Feature: F\n\n \nRule: R\n  Scenario: S\n    Given x\n", 2)]
    public void Should_expect_a_tag_line_on_blank_or_tag_only_lines_above_a_block(string text, int line)
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());

        sut.ParseAndCollectErrors(text, new IdeSupportNullLogger(), out var gherkinDocument, out _);

        gherkinDocument.GetExpectedTokens(line, Substitute.For<ITelemetryService>())
            .Should().Contain(global::Gherkin.TokenType.TagLine);
    }

    [Fact]
    public void Should_provide_parse_result_when_unexpected_end_of_file()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());

        var result = sut.ParseAndCollectErrors(@"
Feature: Addition
@tag
",
            new IdeSupportNullLogger(), out var gherkinDocument, out var errors);
        gherkinDocument.Should().NotBeNull();
        result.Should().BeFalse();
    }

    [Fact]
    public void Should_tolerate_backslash_at_end_of_line_in_DataTable()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());

        var result = sut.ParseAndCollectErrors(@"
Feature: Addition
Scenario: Add two numbers
	When I press
		| foo |
		| bar \
",
            new IdeSupportNullLogger(), out var gherkinDocument, out var errors);
        gherkinDocument.Should().NotBeNull();
        result.Should().BeFalse();
    }

    [Fact]
    public void Should_tolerate_unfinished_DataTable()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());

        var result = sut.ParseAndCollectErrors(@"
Feature: Addition
Scenario: Add two numbers
	When I press
		| foo |
		| bar
",
            new IdeSupportNullLogger(), out var gherkinDocument, out var errors);
        gherkinDocument.Should().NotBeNull();
        result.Should().BeFalse();
    }

    [Fact]
    public void Should_provide_parse_result_when_file_ends_with_open_docstring()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("en-US"),
            Substitute.For<ITelemetryService>());

        var result = sut.ParseAndCollectErrors(@"
Feature: Addition
Scenario: Add two numbers
  Given I have added
    ```
",
            new IdeSupportNullLogger(), out var gherkinDocument, out var errors);
        gherkinDocument.Should().NotBeNull();
        result.Should().BeFalse();
    }

    // Regression tests for issue #955. `_astBuilder` is assigned in Parse() only after the dialect
    // provider / TokenMatcher are constructed, and the error-recovery paths re-enter the builder
    // (GetResultOfInvalid -> GetResult) without guarding it. With an unsupported configured default
    // language the dialect resolution fails at parse time, and the unguarded recovery call used to
    // let a non-parser exception ("Stack empty" / NullReferenceException) escape
    // ParseAndCollectErrors; callers swallowed it, so the user saw an empty result instead of a
    // reported parser error.
    [Fact]
    public void Should_not_throw_for_unsupported_default_language()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("xx-INVALID-not-a-language"),
            Substitute.For<ITelemetryService>());

        var act = () => sut.ParseAndCollectErrors("Feature: F\n", new IdeSupportNullLogger(), out _, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void Should_report_unsupported_default_language_as_parser_error()
    {
        var sut = new IdeSupportGherkinParser(new ReqnrollGherkinDialectProvider("xx-INVALID-not-a-language"),
            Substitute.For<ITelemetryService>());

        var result = sut.ParseAndCollectErrors("Feature: F\n", new IdeSupportNullLogger(),
            out var gherkinDocument, out var errors);

        result.Should().BeFalse();
        gherkinDocument.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().BeOfType<global::Gherkin.NoSuchLanguageException>();
    }
}
