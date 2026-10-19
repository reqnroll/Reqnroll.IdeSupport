using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// The one telemetry capability genuinely shared between every host: reporting an exception.
/// <para>
/// Split out of <see cref="ITelemetryService"/> (which it extends) because that interface's other
/// ~15 members are VS-host-lifecycle concerns (project wizards, welcome/upgrade dialogs) that
/// <c>LSP.Core</c> has no business depending on. Classes that only report a parse exception
/// (<c>IdeSupportGherkinParser</c>, <c>IdeSupportTagParser</c>, <c>CompletionContextResolver</c>) depend on
/// this narrow interface, and its implementations (<c>NullLspTelemetryService</c>,
/// <c>LspErrorTelemetryService</c>) need not stub out ~15 unrelated members.
/// </para>
/// </summary>
public interface IErrorTelemetryService
{
    /// <summary>Records an exception, optionally flagging whether it was fatal.</summary>
    void MonitorError(Exception exception, bool? isFatal = null);
}
