using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// Records the wall-clock duration of an LSP protocol operation for the architecture's Performance
/// Verification "Layer 4" field instrumentation: real-world P95 measured in the live server,
/// emitted via the existing logging path and (optionally, sampled) as a telemetry metric.
/// </summary>
/// <remarks>
/// This is the single cross-cutting sink invoked at each handler boundary. It exists because the
/// four interactive performance targets live on three different registration rails — manual
/// <c>OnRequest</c> delegates (<c>semanticTokens/full</c>), OmniSharp <c>AddHandler</c> handlers
/// (<c>completion</c>, <c>definition</c>) and a MediatR notification push
/// (<c>publishDiagnostics</c>) — so no single MediatR pipeline behavior can cover them all.
/// </remarks>
public interface IOperationDurationRecorder
{
    /// <summary>
    /// Starts a timing scope; the elapsed time is recorded when the returned handle is disposed.
    /// Usage: <c>using var _ = recorder.Measure("textDocument/completion", uri);</c>
    /// </summary>
    /// <param name="operation">The operation label.</param>
    /// <param name="uri">The document the operation concerns, if any.</param>
    /// <param name="detail">
    /// Optional free-form size/state tag (e.g. <c>"cacheDocs=50 cacheSteps=1350"</c>), captured at
    /// call time and appended to the PERF log line. For issue #471-style investigations: lets a
    /// climbing-duration pattern be correlated against the state that grew, without adding a
    /// per-operation-type field to the recorder itself. Cheap to compute is the caller's
    /// responsibility — this sink does not gate or defer it.
    /// </param>
    IDisposable Measure(string operation, DocumentUri? uri = null, string? detail = null);

    /// <summary>
    /// Records an already-measured duration for <paramref name="operation"/>. Use this overload
    /// when the operation label (or <paramref name="detail"/>) is only known after the work runs
    /// (e.g. keyword vs. step completion; a reconcile's actual file/step counts).
    /// </summary>
    void Record(string operation, double elapsedMs, DocumentUri? uri = null, string? detail = null);
}
