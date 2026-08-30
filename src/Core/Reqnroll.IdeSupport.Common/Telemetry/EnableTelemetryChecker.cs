using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>EnableTelemetryChecker</summary>
public class EnableTelemetryChecker : IEnableTelemetryChecker
{
    /// <summary>Name of the environment variable that opts out of (or into) telemetry transmission.</summary>
    public const string ReqnrollTelemetryEnvironmentVariable = "REQNROLL_TELEMETRY_ENABLED";

    /// <summary>Determines whether telemetry transmission is enabled, based on the <see cref="ReqnrollTelemetryEnvironmentVariable"/> environment variable.</summary>
    public bool IsEnabled()
    {
        var reqnrollTelemetry = Environment.GetEnvironmentVariable(ReqnrollTelemetryEnvironmentVariable);
        return reqnrollTelemetry == null || reqnrollTelemetry.Equals("1");
    }
}
