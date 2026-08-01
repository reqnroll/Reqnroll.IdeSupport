using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RenameStep;

/// <summary>
/// Splitting a picker label into the keyword prefix and step text
/// (<see cref="RenameStepLabelParser.ExtractStepTextFromLabel"/>), used to seed the rename
/// input box when the server did not supply a raw expression.
/// </summary>
public class ExtractStepTextFromLabelTests
{
    [Fact]
    public void A_label_with_a_keyword_prefix_returns_the_text_after_the_first_space()
    {
        RenameStepLabelParser.ExtractStepTextFromLabel("Given I press add").Should().Be("I press add");
    }

    [Fact]
    public void A_label_without_a_space_is_returned_unchanged()
    {
        RenameStepLabelParser.ExtractStepTextFromLabel("GivenIPressAdd").Should().Be("GivenIPressAdd");
    }

    [Fact]
    public void A_label_consisting_of_only_the_prefix_and_a_trailing_space_is_returned_unchanged()
    {
        RenameStepLabelParser.ExtractStepTextFromLabel("Given ").Should().Be("Given ");
    }

    [Fact]
    public void An_empty_label_is_returned_unchanged()
    {
        RenameStepLabelParser.ExtractStepTextFromLabel("").Should().Be("");
    }
}
