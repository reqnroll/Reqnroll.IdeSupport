using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.Text;
using JetBrains.UI.Icons;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

[ProjectFileType(typeof(FeatureProjectFileType))]
public class FeatureProjectFileLanguageService : ProjectFileLanguageService
{
    public FeatureProjectFileLanguageService(FeatureProjectFileType projectFileType) : base(projectFileType)
    {
    }

    public override IconId Icon => null;

    protected override PsiLanguageType PsiLanguageType => FeatureLanguage.Instance.NotNull();

    public override ILexerFactory GetMixedLexerFactory(ISolution solution, IBuffer buffer, [CanBeNull] IPsiSourceFile sourceFile = null) =>
        new FeatureLexerFactory();
}
