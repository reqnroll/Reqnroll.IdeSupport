using System;

namespace Reqnroll.IdeSupport.Common.Telemetry;

/// <summary>
/// The one telemetry capability genuinely shared between every host: reporting an exception.
/// <para>
/// Split out of <see cref="ITelemetryService"/> (which it extends) because that interface's other
/// ~15 members are VS-host-lifecycle concerns (project wizards, welcome/upgrade dialogs) that
/// <c>LSP.Core</c> has no business depending on — before this split, every <c>LSP.Core</c> class
/// that only ever needed to report a parse exception (<c>IdeSupportGherkinParser</c>,
/// <c>IdeSupportTagParser</c>, <c>CompletionContextResolver</c>) took a full <see cref="ITelemetryService"/>
/// dependency, and every implementation of it (<c>NullLspTelemetryService</c>,
/// <c>LspErrorTelemetryService</c>) had to stub out those ~15 unrelated members just to satisfy the
/// interface (issue #255/#259).
/// </para>
/// </summary>
public interface IErrorTelemetryService
{
    /// <summary>Records an exception, optionally flagging whether it was fatal.</summary>
    void MonitorError(Exception exception, bool? isFatal = null);
}
