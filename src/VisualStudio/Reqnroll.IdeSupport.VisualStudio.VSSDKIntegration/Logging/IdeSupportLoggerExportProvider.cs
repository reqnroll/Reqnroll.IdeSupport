using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Logging;

/// <summary>
/// MEF export of the shared <see cref="IIdeSupportLogger"/> for the VSSDK/MEF composition root
/// (issue #84: previously exported but never populated with child loggers, so every MEF import of
/// this type was silently a no-op). Exports <see cref="ExtensionHostLogger.Instance"/>, the same
/// instance <c>ExtensionEntrypoint.InitializeServices</c> registers with VS.Extensibility DI, so both
/// composition roots write through one file logger and one "Reqnroll" Output Window pane
/// (issue #748).
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
    /// <summary>The process-wide extension logger; see <see cref="ExtensionHostLogger"/>.</summary>
    [Export(typeof(IIdeSupportLogger))]
    public IIdeSupportLogger Logger => ExtensionHostLogger.Instance;
}
