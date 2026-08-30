namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

/// <summary>
/// Minimal telemetry surface needed by SafeDispatcherTimer for error reporting.
/// Kept separate from IWizardTelemetry which covers wizard-specific events.
/// </summary>
public interface IWizardTelemetryLogger
{
    void MonitorError(Exception exception, bool? isFatal = null);
}
