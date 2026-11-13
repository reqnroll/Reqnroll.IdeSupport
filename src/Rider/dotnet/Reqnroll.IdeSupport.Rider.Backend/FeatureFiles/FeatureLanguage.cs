using JetBrains.Annotations;
using JetBrains.ReSharper.Psi;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>
/// Backend language for <c>.feature</c> files. It carries no Gherkin knowledge: the language server does
/// all the parsing, and the backend only needs the language so a breakpoint provider can attach to it.
/// </summary>
[LanguageDefinition(Name)]
public class FeatureLanguage : KnownLanguage
{
    public new const string Name = "REQNROLL_FEATURE";

    [CanBeNull]
    [UsedImplicitly]
    public static FeatureLanguage Instance { get; private set; }

    private FeatureLanguage() : base(Name, "Reqnroll Feature")
    {
    }
}
