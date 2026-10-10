#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Parsing.CSharp;

public class CSharpStringLiteralTests
{
    [Theory]
    [InlineData("plain",       "\"plain\"")]
    [InlineData("say \"hi\"",  "\"say \\\"hi\\\"\"")]
    [InlineData("a \\ b",      "\"a \\\\ b\"")]
    [InlineData("a\\(b",       "\"a\\\\(b\"")]
    [InlineData("a\nb\r\tc",   "\"a\\nb\\r\\tc\"")]
    public void Regular_literal_escapes_backslash_quote_and_control_characters(string text, string expected)
        => CSharpStringLiteral.Format(text, isVerbatim: false).Should().Be(expected);

    [Theory]
    [InlineData("plain",       "@\"plain\"")]
    [InlineData("say \"hi\"",  "@\"say \"\"hi\"\"\"")]
    [InlineData("^I have (\\d+) cukes$", "@\"^I have (\\d+) cukes$\"")]
    public void Verbatim_literal_doubles_quotes_and_leaves_backslashes_alone(string text, string expected)
        => CSharpStringLiteral.Format(text, isVerbatim: true).Should().Be(expected);
}
