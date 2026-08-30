using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Classification;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// The fixed Reqnroll semantic-token legend and the <see cref="IdeSupportTag"/>→token mapping.
/// </summary>
/// <remarks>
/// <para>
/// The server advertises one <b>shared</b> set of custom semantic token types whose names
/// (<c>reqnroll.keyword</c>, <c>reqnroll.tag</c>, …) match the custom
/// <c>ClassificationTypeDefinition</c> names used by the existing Reqnroll Visual Studio
/// extension (<c>IdeSupportClassifications</c>).  The legend is identical for every IDE client;
/// it is the <i>client's</i> responsibility to map each legend name to a concrete editor colour:
/// </para>
/// <list type="bullet">
///   <item><b>Visual Studio</b> re-uses the <c>IdeSupportClassifications</c> MEF exports, so each
///         legend name resolves to the classification of the same name.</item>
///   <item><b>VS Code</b> maps the names via <c>semanticTokenScopes</c> / <c>configurationDefaults</c>.</item>
///   <item><b>Rider</b> registers a <c>TextAttributesKey</c> per name and maps via the LSP descriptor.</item>
/// </list>
/// <para>
/// Because the legend no longer varies per IDE, there is no profile abstraction or
/// <c>--ide</c>-based selection here: the definition is a single static contract.  No token
/// modifiers are used — each Reqnroll concept is its own classification/colour, so the
/// distinction is carried entirely by the token type name.
/// </para>
/// </remarks>
public static class ReqnrollSemanticTokens
{
    // ── Token type indices (must match ReqnrollClassificationTypeNames.Ordered) ──
    private enum T
    {
        Keyword                    = 0,
        Tag                        = 1,
        Description                = 2,
        Comment                    = 3,
        DocString                  = 4,
        DataTable                  = 5,
        DataTableHeader            = 6,
        StepParameter              = 7,
        ScenarioOutlinePlaceholder = 8,
        UndefinedStep              = 9,
        AmbiguousStep              = 10,
    }

    /// <summary>
    /// The token-type legend advertised to every IDE client in the <c>initialize</c> response.
    /// </summary>
    public static SemanticTokensLegend Legend { get; } = new SemanticTokensLegend
    {
        TokenTypes = new Container<SemanticTokenType>(
            ReqnrollClassificationTypeNames.Ordered
                .Select(name => new SemanticTokenType(name))
                .ToArray()),
        // No modifiers: colour is carried by the token type name alone.
        TokenModifiers = new Container<SemanticTokenModifier>(),
    };

    /// <summary>
    /// Maps a <see cref="IdeSupportTag"/> to a zero-based index into <see cref="Legend"/> token
    /// types and a modifier bitmask.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the tag should produce a semantic token; <see langword="false"/>
    /// if it should be silently skipped (block/container tags and unclassified artifacts).
    /// </returns>
    public static bool TryGetToken(IdeSupportTag tag, out int tokenTypeIndex, out int tokenModifierBitset)
    {
        tokenModifierBitset = 0;

        switch (tag.Type)
        {
            case IdeSupportTagTypes.StepKeyword:
            case IdeSupportTagTypes.DefinitionLineKeyword:
                tokenTypeIndex = (int)T.Keyword;
                return true;

            case IdeSupportTagTypes.Tag:
                tokenTypeIndex = (int)T.Tag;
                return true;

            case IdeSupportTagTypes.Description:
                tokenTypeIndex = (int)T.Description;
                return true;

            case IdeSupportTagTypes.Comment:
                tokenTypeIndex = (int)T.Comment;
                return true;

            case IdeSupportTagTypes.DocString:
                tokenTypeIndex = (int)T.DocString;
                return true;

            case IdeSupportTagTypes.DataTable:
                tokenTypeIndex = (int)T.DataTable;
                return true;

            case IdeSupportTagTypes.DataTableHeader:
                tokenTypeIndex = (int)T.DataTableHeader;
                return true;

            case IdeSupportTagTypes.StepParameter:
                tokenTypeIndex = (int)T.StepParameter;
                return true;

            case IdeSupportTagTypes.ScenarioOutlinePlaceholder:
                tokenTypeIndex = (int)T.ScenarioOutlinePlaceholder;
                return true;

            case IdeSupportTagTypes.UndefinedStep:
                tokenTypeIndex = (int)T.UndefinedStep;
                return true;

            case IdeSupportTagTypes.AmbiguousStep:
                tokenTypeIndex = (int)T.AmbiguousStep;
                return true;

            // DefinedStep and BindingError have no Reqnroll classification — they render as
            // normal step text in the existing VS extension, so emit no token here.  Block /
            // container tags (FeatureBlock, etc.), ParserError and Document are also skipped.
            default:
                tokenTypeIndex = 0;
                return false;
        }
    }
}
