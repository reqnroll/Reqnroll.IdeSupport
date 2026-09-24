using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Logging;

/// <summary>
/// The one process-wide <see cref="IIdeSupportLogger"/> for the Run CodeLens components that run
/// out-of-process in VS's CodeLens ServiceHub host (<c>RunTestCodeLensDataPointProvider</c>, its
/// data points, and <c>RunTestOutcomeBridge</c>).
/// </summary>
/// <remarks>
/// That host is a different process from <c>devenv.exe</c>, so it can't share
/// <see cref="ExtensionHostLogger"/>. It writes to its own <c>codelens-sh</c> role
/// (<c>reqnroll-vs-codelens-sh-*-&lt;pid&gt;.log</c>) rather than <c>ext</c>, so the file names
/// say which process wrote them instead of relying on the PID alone. File-only: there is no VS
/// shell in this process for <see cref="VsOutputPaneLogger"/> to write to. Before issue #748 the
/// provider and the bridge each built their own <see cref="SynchronousFileLogger"/> for the same
/// file, so concurrent writes could silently drop a line.
/// </remarks>
internal static class CodeLensHostLogger
{
    /// <summary>The log-file role for the CodeLens ServiceHub host.</summary>
    internal const string FileRole = "codelens-sh";

    /// <summary>Threshold for the CodeLens host's log file.</summary>
    internal const TraceLevel FileLevel = TraceLevel.Verbose;

    /// <summary>Gets the shared file logger for the CodeLens ServiceHub host.</summary>
    internal static IIdeSupportLogger Instance { get; } = new SynchronousFileLogger("vs", FileRole, FileLevel);
}
