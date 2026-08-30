using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;

/// <summary>
/// Visual Studio's MEF-exported <see cref="IGuidanceConfiguration"/> implementation.
/// </summary>
[Export(typeof(IGuidanceConfiguration))]
public class VsGuidanceConfiguration : GuidanceConfiguration
{

}
