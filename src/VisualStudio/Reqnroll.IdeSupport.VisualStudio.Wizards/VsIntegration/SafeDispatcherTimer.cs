// Ported from Reqnroll.VisualStudio\SafeDispatcherTimer.cs
// Only used by VsSimulatedItemAddWizardBase — kept in VsIntegration because
// it has a hard WPF (DispatcherTimer / MessageBox) dependency.
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Wraps a WPF <see cref="DispatcherTimer"/>, ensuring exceptions raised by the
/// scheduled action are logged (or shown in a message box when no logger is available)
/// instead of crashing the dispatcher.
/// </summary>
public class SafeDispatcherTimer
{
    private readonly Func<bool> _action;
    private readonly DispatcherTimer _dispatcherTimer;
    private readonly IIdeSupportLogger? _logger;
    private readonly IWizardTelemetryLogger? _telemetryService;

    private SafeDispatcherTimer(int intervalSeconds, IIdeSupportLogger? logger, IWizardTelemetryLogger? telemetryService,
        Action action)
    {
        _action = () => { action(); return false; };
        _logger = logger;
        _telemetryService = telemetryService;
        _dispatcherTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(intervalSeconds),
            DispatcherPriority.ContextIdle,
            DispatcherTick,
            Dispatcher.CurrentDispatcher);
    }

    private SafeDispatcherTimer(int intervalSeconds, IIdeSupportLogger? logger, IWizardTelemetryLogger? telemetryService,
        Func<bool> action)
    {
        _action = action;
        _logger = logger;
        _telemetryService = telemetryService;
        _dispatcherTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(intervalSeconds),
            DispatcherPriority.ContextIdle,
            DispatcherTick,
            Dispatcher.CurrentDispatcher);
    }

    /// <summary>
    /// Creates a timer that fires <paramref name="action"/> once after
    /// <paramref name="intervalSeconds"/> and then stops.
    /// </summary>
    public static SafeDispatcherTimer CreateOneTime(int intervalSeconds, IIdeSupportLogger? logger,
        IWizardTelemetryLogger? telemetryService, Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return new SafeDispatcherTimer(intervalSeconds, logger, telemetryService, action);
    }

    /// <summary>
    /// Creates a timer that fires <paramref name="action"/> every
    /// <paramref name="intervalSeconds"/> as long as it returns <c>true</c>.
    /// </summary>
    public static SafeDispatcherTimer CreateContinuing(int intervalSeconds, IIdeSupportLogger? logger,
        IWizardTelemetryLogger? telemetryService, Func<bool> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return new SafeDispatcherTimer(intervalSeconds, logger, telemetryService, action);
    }

    /// <summary>Starts the underlying dispatcher timer.</summary>
    public void Start() => _dispatcherTimer.Start();

    private void DispatcherTick(object? sender, EventArgs e)
    {
        try
        {
            _dispatcherTimer.Stop();
            bool doContinue = _action();
            if (doContinue)
                _dispatcherTimer.Start();
        }
        catch (Exception ex)
        {
            _telemetryService?.MonitorError(ex);
            _logger?.LogException(ex);
            if (_logger == null)
                MessageBox.Show("Unhandled exception: " + ex, "Reqnroll error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
