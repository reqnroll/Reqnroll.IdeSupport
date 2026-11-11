using JetBrains.Diagnostics;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Tree;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>Root of the stub tree: a file with a single text token as its child.</summary>
public class FeatureFile : FileElementBase
{
    public override NodeType NodeType => FeatureNodeTypes.File;

    public override PsiLanguageType Language => FeatureLanguage.Instance.NotNull();

    public override string ToString() => nameof(FeatureFile);
}
