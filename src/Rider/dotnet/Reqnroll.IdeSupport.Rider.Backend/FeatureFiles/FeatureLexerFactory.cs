using JetBrains.ReSharper.Psi.Parsing;
using JetBrains.Text;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

public class FeatureLexerFactory : ILexerFactory
{
    public ILexer CreateLexer(IBuffer buffer) => new FeatureLexer(buffer);
}
