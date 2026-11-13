using JetBrains.Annotations;
using JetBrains.ProjectModel;

namespace Reqnroll.IdeSupport.Rider.Backend.FeatureFiles;

/// <summary>
/// Gives Rider's backend a project file type for <c>.feature</c> files. Without one the backend has no
/// primary PSI file for the file, and so never offers a breakpoint on any of its lines.
/// </summary>
[ProjectFileTypeDefinition(Name)]
public class FeatureProjectFileType : KnownProjectFileType
{
    public new const string Name = "REQNROLL_FEATURE";
    public const string FeatureExtension = ".feature";

    [CanBeNull]
    [UsedImplicitly]
    public new static FeatureProjectFileType Instance { get; private set; }

    private FeatureProjectFileType() : base(Name, "Reqnroll Feature", new[] { FeatureExtension })
    {
    }
}
