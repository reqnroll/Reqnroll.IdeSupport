using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Tree;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Text;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

public class FeatureToken : BoundToBufferLeafElement, ITokenNode
{
    private readonly TokenNodeType _tokenType;

    public FeatureToken(TokenNodeType nodeType, [NotNull] IBuffer buffer, TreeOffset startOffset, TreeOffset endOffset)
        : base(nodeType, buffer, startOffset, endOffset)
    {
        _tokenType = nodeType;
    }

    public override PsiLanguageType Language => FeatureLanguage.Instance.NotNull();

    public TokenNodeType GetTokenType() => _tokenType;

    public override string ToString() => $"FeatureToken({GetTreeStartOffset()},{Length})";
}
