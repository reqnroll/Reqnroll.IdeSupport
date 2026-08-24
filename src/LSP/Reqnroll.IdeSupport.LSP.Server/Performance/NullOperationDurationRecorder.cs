using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// No-op <see cref="IOperationDurationRecorder"/>. Used as the default when a handler is
/// constructed without a recorder (unit tests, or a host that does not wire instrumentation),
/// keeping field instrumentation non-intrusive to feature logic.
/// </summary>
public sealed class NullOperationDurationRecorder : IOperationDurationRecorder
{
    /// <summary>The shared singleton no-op recorder instance.</summary>
    public static readonly NullOperationDurationRecorder Instance = new();

    private NullOperationDurationRecorder() { }

    /// <inheritdoc/>
    public IDisposable Measure(string operation, DocumentUri? uri = null, string? detail = null) => NullScope.Instance;

    /// <inheritdoc/>
    public void Record(string operation, double elapsedMs, DocumentUri? uri = null, string? detail = null) { }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
