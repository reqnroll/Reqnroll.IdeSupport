using Reqnroll.IdeSupport.LSP.Server.Logging;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Logging;

/// <summary>
/// Issue #791: the server's log files are named after the <c>--ide</c> value. Rider used to fall through
/// to the generic <c>lsp</c> prefix, contradicting the documented <c>reqnroll-rider-*</c> naming.
/// </summary>
public class IdeLogPrefixTests
{
    [Theory]
    [InlineData("visualstudio", "vs")]
    [InlineData("vscode", "vscode")]
    [InlineData("rider", "rider")]
    public void Known_ide_values_map_to_their_documented_prefix(string ide, string expected) =>
        IdeLogPrefix.From(ide).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-unknown-ide")]
    [InlineData("Rider")] // --ide is matched case-sensitively, as before; a miss must not be coerced onto a known IDE
    public void Absent_or_unrecognized_ide_falls_back_to_the_neutral_prefix(string? ide) =>
        IdeLogPrefix.From(ide).Should().Be("lsp");
}
