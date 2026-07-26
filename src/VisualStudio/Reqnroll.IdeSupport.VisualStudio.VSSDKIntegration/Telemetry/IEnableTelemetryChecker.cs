using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;


/// <summary>
/// Visual Studio's MEF-exported <see cref="IEnableTelemetryChecker"/> implementation.
/// </summary>
[Export(typeof(IEnableTelemetryChecker))]
public class EnableTelemetryChecker : Reqnroll.IdeSupport.Common.Telemetry.EnableTelemetryChecker { }
