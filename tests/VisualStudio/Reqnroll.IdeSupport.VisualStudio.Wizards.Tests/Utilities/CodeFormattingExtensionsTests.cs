using Reqnroll.IdeSupport.VisualStudio.Wizards.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.Utilities;

public class CodeFormattingExtensionsTests
{
    [Fact]
    public void ToIdentifier_leaves_an_already_valid_identifier_unchanged_apart_from_casing()
    {
        "MyProject".ToIdentifier().Should().Be("MyProject");
    }

    [Fact]
    public void ToIdentifier_upper_cases_the_first_letter_after_a_punctuation_or_whitespace_run()
    {
        "my project".ToIdentifier().Should().Be("MyProject");
    }

    [Fact]
    public void ToIdentifier_replaces_dots_and_hyphens_with_an_underscore()
    {
        "My-Project.Name".ToIdentifier().Should().Be("My_Project_Name");
    }

    [Fact]
    public void ToIdentifier_prefixes_a_leading_digit_with_an_underscore()
    {
        "1Project".ToIdentifier().Should().Be("_1Project");
    }

    [Fact]
    public void ToIdentifier_removes_single_and_double_quotes()
    {
        "My\"Cool'Project".ToIdentifier().Should().Be("MyCoolProject");
    }

    [Fact]
    public void ToIdentifier_replaces_accented_Latin_characters_with_their_unaccented_equivalent()
    {
        "Café".ToIdentifier().Should().Be("Cafe");
    }

    [Fact]
    public void ToIdentifierCamelCase_lower_cases_only_the_first_character()
    {
        "MyProject".ToIdentifierCamelCase().Should().Be("myProject");
    }

    [Fact]
    public void RemoveQuotationCharacters_strips_single_and_double_quotes()
    {
        CodeFormattingExtensions.RemoveQuotationCharacters("a'b\"c").Should().Be("abc");
    }

    [Fact]
    public void TrimEllipse_leaves_short_text_unchanged()
    {
        "short".TrimEllipse(10).Should().Be("short");
    }

    [Fact]
    public void TrimEllipse_truncates_long_text_and_appends_an_ellipsis()
    {
        "a very long piece of text".TrimEllipse(10).Should().Be("a very ...");
    }
}
