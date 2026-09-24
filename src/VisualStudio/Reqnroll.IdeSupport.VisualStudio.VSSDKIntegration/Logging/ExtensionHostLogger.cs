using System.Diagnostics;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Logging;

/// <summary>
/// The one process-wide <see cref="IIdeSupportLogger"/> for the extension's in-proc
/// (<c>devenv.exe</c>) side, shared by both of its composition roots: VS.Extensibility DI
/// (<c>ExtensionEntrypoint.InitializeServices</c>) and VSSDK MEF
/// (<see cref="IdeSupportLoggerExportProvider"/>).
/// </summary>
/// <remarks>
/// A static singleton, like <c>SemanticTokenClassificationStore.Instance</c>, because neither
/// composition root can resolve the other's instances. Before issue #748 each root built its own
/// composite: two <see cref="SynchronousFileLogger"/>s appended to the same
/// <c>reqnroll-vs-ext-*-&lt;pid&gt;.log</c> file under separate per-instance write locks, so
/// concurrent writes from the two roots hit a sharing violation and one line was silently dropped.
/// The two roots also disagreed on the file level (Warning vs. Info), and only the DI side wrote to
/// the "Reqnroll" Output Window pane (<see cref="VsOutputPaneLogger"/>).
/// Do not construct another <c>("vs", "ext")</c> <see cref="SynchronousFileLogger"/> in this
/// process - use this instance.
/// </remarks>
public static class ExtensionHostLogger
{
    /// <summary>The log-file role for the in-proc extension (<c>reqnroll-vs-ext-*.log</c>).</summary>
    internal const string FileRole = "ext";

    /// <summary>Threshold for the in-proc extension's log file.</summary>
    internal const TraceLevel FileLevel = TraceLevel.Info;

    /// <summary>Gets the shared logger: debug output, the <c>ext</c> log file, and the "Reqnroll" Output Window pane.</summary>
    public static IIdeSupportLogger Instance { get; } = new IdeSupportCompositeLogger()
        .Add(new IdeSupportDebugLogger())
        .Add(new SynchronousFileLogger("vs", FileRole, FileLevel))
        .Add(new VsOutputPaneLogger());
}
