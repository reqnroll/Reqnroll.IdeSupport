using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Tree;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Text;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>The single kind of token the stub lexer produces: the whole file text.</summary>
public class FeatureTokenType : TokenNodeType
{
    public static readonly FeatureTokenType Text = new FeatureTokenType("REQNROLL_FEATURE_TEXT", 5101);

    private FeatureTokenType(string name, int index) : base(name, index)
    {
    }

    public override LeafElementBase Create(IBuffer buffer, TreeOffset startOffset, TreeOffset endOffset) =>
        new FeatureToken(this, buffer, startOffset, endOffset);

    public override bool IsWhitespace => false;
    public override bool IsComment => false;
    public override bool IsStringLiteral => false;
    public override bool IsConstantLiteral => false;
    public override bool IsIdentifier => false;
    public override bool IsKeyword => false;
    public override string TokenRepresentation => "feature text";
}
