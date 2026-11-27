using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Application.Components;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Caches2;
using JetBrains.ReSharper.Psi.Impl;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.ReSharper.Psi.Tree;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>
/// Minimal language service: just enough for Rider's backend to build a (one-token) PSI file for a
/// <c>.feature</c> file. No caches, type declarations or formatter.
/// </summary>
[Language(typeof(FeatureLanguage))]
public class FeatureLanguageService : LanguageService
{
    public FeatureLanguageService(
        [NotNull] FeatureLanguage language,
        [NotNull] ILazy<IConstantValueService> constantValueService)
        : base(language, constantValueService, null)
    {
    }

    public override ILexerFactory GetPrimaryLexerFactory() => new FeatureLexerFactory();

    public override ILexer CreateFilteringLexer(ILexer lexer) => lexer;

    public override IParser CreateParser(ILexer lexer, IPsiModule module, IPsiSourceFile sourceFile) => new FeatureParser(lexer);

    public override IEnumerable<ITypeDeclaration> FindTypeDeclarations(IFile file) => new ITypeDeclaration[0];

    public override ILanguageCacheProvider CacheProvider => null;

    public override bool IsCaseSensitive => true;

    public override bool SupportTypeMemberCache => false;

    public override ITypePresenter TypePresenter => DefaultTypePresenter.Instance;
}
