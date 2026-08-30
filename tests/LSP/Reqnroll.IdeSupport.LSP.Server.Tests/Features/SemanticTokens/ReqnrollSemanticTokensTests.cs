using Reqnroll.IdeSupport.Common.Classification;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;





namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

public class ReqnrollSemanticTokensTests
{
    // ── Legend ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Legend_declares_the_custom_reqnroll_token_types_in_order_and_no_modifiers()
    {
        var advertised = ReqnrollSemanticTokens.Legend.TokenTypes.Select(t => t.ToString()).ToList();
        advertised.Should().Equal(ReqnrollClassificationTypeNames.Ordered);

        // Colour is carried by the token type name alone — no modifiers.
        ReqnrollSemanticTokens.Legend.TokenModifiers.Should().BeEmpty();
    }

    // ── TryGetToken ────────────────────────────────────────────────────────────

    private static readonly IGherkinTextSnapshot Snapshot = new StubSnapshot();

    private static IdeSupportTag Tag(string type) =>
        new(type, new GherkinRange(Snapshot, 0, 1));

    [Theory]
    [InlineData(IdeSupportTagTypes.StepKeyword, ReqnrollClassificationTypeNames.Keyword)]
    [InlineData(IdeSupportTagTypes.DefinitionLineKeyword, ReqnrollClassificationTypeNames.Keyword)]
    [InlineData(IdeSupportTagTypes.Tag, ReqnrollClassificationTypeNames.Tag)]
    [InlineData(IdeSupportTagTypes.Description, ReqnrollClassificationTypeNames.Description)]
    [InlineData(IdeSupportTagTypes.Comment, ReqnrollClassificationTypeNames.Comment)]
    [InlineData(IdeSupportTagTypes.DocString, ReqnrollClassificationTypeNames.DocString)]
    [InlineData(IdeSupportTagTypes.DataTable, ReqnrollClassificationTypeNames.DataTable)]
    [InlineData(IdeSupportTagTypes.DataTableHeader, ReqnrollClassificationTypeNames.DataTableHeader)]
    [InlineData(IdeSupportTagTypes.StepParameter, ReqnrollClassificationTypeNames.StepParameter)]
    [InlineData(IdeSupportTagTypes.ScenarioOutlinePlaceholder, ReqnrollClassificationTypeNames.ScenarioOutlinePlaceholder)]
    [InlineData(IdeSupportTagTypes.UndefinedStep, ReqnrollClassificationTypeNames.UndefinedStep)]
    [InlineData(IdeSupportTagTypes.AmbiguousStep, ReqnrollClassificationTypeNames.AmbiguousStep)]
    public void TryGetToken_maps_known_leaf_tags_to_the_matching_reqnroll_token_type(string tagType, string expectedName)
    {
        ReqnrollSemanticTokens.TryGetToken(Tag(tagType), out var typeIndex, out var modBits).Should().BeTrue();

        ReqnrollSemanticTokens.Legend.TokenTypes.ElementAt(typeIndex).ToString().Should().Be(expectedName);
        modBits.Should().Be(0, "the custom mapping does not use token modifiers");
    }

    [Theory]
    // DefinedStep and BindingError have no Reqnroll classification — they render as normal
    // step text, so the server emits no token for them.
    [InlineData(IdeSupportTagTypes.DefinedStep)]
    [InlineData(IdeSupportTagTypes.BindingError)]
    [InlineData("SomeUnmappedContainerTag")]
    public void TryGetToken_returns_false_for_unclassified_or_container_tags(string tagType)
    {
        ReqnrollSemanticTokens.TryGetToken(Tag(tagType), out var typeIndex, out var modBits).Should().BeFalse();
        typeIndex.Should().Be(0);
        modBits.Should().Be(0);
    }

    // ── Minimal snapshot stub (TryGetToken never reads the range) ───────────────

    private sealed class StubSnapshot : IGherkinTextSnapshot
    {
        public int Version => 1;
        public int Length => 1;
        public int LineCount => 1;
        public string GetText() => " ";
        public IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber) => new StubLine();

        private sealed class StubLine : IGherkinTextSnapshotLine
        {
            public int LineNumber => 0;
            public int Start => 0;
            public int End => 1;
            public string GetText() => " ";
        }
    }
}
