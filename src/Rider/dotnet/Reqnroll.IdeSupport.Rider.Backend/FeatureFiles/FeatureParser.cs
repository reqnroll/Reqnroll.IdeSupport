using JetBrains.Lifetimes;
using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.TreeBuilder;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>Builds a tree of one file node holding every token the stub lexer produced.</summary>
public class FeatureParser : IParser
{
    private readonly ILexer _lexer;

    public FeatureParser(ILexer lexer)
    {
        _lexer = lexer;
    }

    public IFile ParseFile()
    {
        using var lifetimeDefinition = Lifetime.Define();
        var builder = new PsiBuilder(_lexer, FeatureNodeTypes.File, null, lifetimeDefinition.Lifetime);
        var fileMarker = builder.Mark();
        while (!builder.Eof())
            builder.AdvanceLexer();
        builder.Done(fileMarker, FeatureNodeTypes.File, null);
        return (IFile)builder.BuildTree();
    }
}
