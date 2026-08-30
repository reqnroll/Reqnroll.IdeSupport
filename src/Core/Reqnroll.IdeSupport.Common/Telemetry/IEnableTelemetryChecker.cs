namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>IEnableTelemetryChecker</summary>
public interface IEnableTelemetryChecker
{
    /// <summary>Determines whether telemetry transmission is currently enabled.</summary>
    bool IsEnabled();
}
