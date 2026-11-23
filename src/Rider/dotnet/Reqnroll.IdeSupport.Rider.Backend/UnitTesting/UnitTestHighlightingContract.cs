namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// The shape of Rider's <c>UnitTestElementHighlighting</c> that <see cref="FeatureTestGutterStage"/> creates by
/// reflection. The type is internal to <c>JetBrains.ReSharper.UnitTestFramework</c>, so nothing checks it at compile
/// time; the backend tests check these names against the Rider SDK the module is built with, so a
/// <c>riderSdkVersion</c> bump that changes them fails the build instead of losing the gutter marks at run time.
/// </summary>
public static class UnitTestHighlightingContract
{
    /// <summary>The assembly that declares the highlighting.</summary>
    public const string AssemblyName = "JetBrains.ReSharper.UnitTestFramework";

    /// <summary>Full name of the internal highlighting type.</summary>
    public const string TypeName =
        "JetBrains.ReSharper.UnitTestFramework.Exploration.Daemon.Highlighting.UnitTestElementHighlighting";

    /// <summary>Full names of the constructor's parameter types, in order.</summary>
    public static readonly string[] ConstructorParameterTypeNames =
    {
        "JetBrains.ReSharper.UnitTestFramework.Exploration.Daemon.IUnitTestLikeElementDisposition",
        "JetBrains.DocumentModel.DocumentRange",
    };

    /// <summary>The interface the highlighting must implement for the daemon to accept it.</summary>
    public const string HighlightingInterfaceName = "JetBrains.ReSharper.Feature.Services.Daemon.IHighlighting";
}
