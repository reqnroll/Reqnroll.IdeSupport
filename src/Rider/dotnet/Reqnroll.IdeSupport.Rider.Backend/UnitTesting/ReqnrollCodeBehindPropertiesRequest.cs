using System.Collections.Generic;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel.Properties;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Asks Rider's project model to evaluate the MSBuild properties that decide where Reqnroll puts code-behind files
/// (<see cref="CodeBehindPaths"/>), so <see cref="FeatureTestGutterStage"/> can read them per target framework.
/// </summary>
[ShellComponent(Instantiation.DemandAnyThreadSafe)]
public class ReqnrollCodeBehindPropertiesRequest : IProjectPropertiesRequest
{
    public IEnumerable<string> RequestedProperties => new[]
    {
        CodeBehindPaths.UseIntermediateOutputPathProperty,
        CodeBehindPaths.IntermediateOutputPathProperty,
    };
}
