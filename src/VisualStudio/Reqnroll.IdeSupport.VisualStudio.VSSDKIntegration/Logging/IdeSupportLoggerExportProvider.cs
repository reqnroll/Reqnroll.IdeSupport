using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Logging;

/// <summary>
/// MEF export provider for the single, shared <see cref="IIdeSupportLogger"/> sink used by the
/// legacy VSSDK/MEF composition root (issue #84): previously exported but never populated with
/// child loggers, so every MEF import of this type was silently a no-op. Wires the same
/// debug-output + synchronous-file-logger pair (at the same default level) used by the
/// Extensibility-SDK side (see <c>ExtensionEntrypoint.InitializeServices</c>), so both composition
/// roots share one consistent default instead of each having their own ad-hoc loggers.
/// </summary>
/// <remarks>
/// A property export (issue #626) rather than a subclass of
/// <see cref="Reqnroll.IdeSupport.Common.Logging.IdeSupportCompositeLogger"/> - the two classes
/// sharing one simple name (<c>IdeSupportCompositeLogger</c>) forced every consumer in this
/// project to exclude <c>Reqnroll.IdeSupport.VisualStudio.Logging</c> from its global usings to
/// avoid ambiguity with <c>Common.Logging</c>. Importers now depend on the
/// <see cref="IIdeSupportLogger"/> abstraction directly (see <c>VsIdeScope</c>,
/// <c>TelemetryTransmitter</c>) instead of this VSSDK-specific concrete type, which no longer
/// exists.
/// </remarks>
public class IdeSupportLoggerExportProvider
{
    /// <summary>The shared composite logger sink, composed once per MEF composition.</summary>
    [Export(typeof(IIdeSupportLogger))]
    public IIdeSupportLogger Logger { get; } = new IdeSupportCompositeLogger()
        .Add(new IdeSupportDebugLogger())
        .Add(new SynchronousFileLogger("vs", "ext", TraceLevel.Info));
}
