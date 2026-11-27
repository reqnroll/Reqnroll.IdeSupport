#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Opens <see cref="ReqnrollProjectTelemetryGate"/> when the server sends
/// <c>reqnroll/reqnrollProjectDetected</c>, releasing the telemetry held until the solution was
/// known to contain a project that uses Reqnroll. Placed before the telemetry interceptor on the
/// receive pipeline so the gate is open before any server event that follows it is forwarded.
/// </summary>
internal sealed class ReqnrollProjectDetectedInterceptor : ILspMessageInterceptor
{
    private readonly ReqnrollProjectTelemetryGate _gate;

    public ReqnrollProjectDetectedInterceptor(ReqnrollProjectTelemetryGate? gate = null)
    {
        _gate = gate ?? ReqnrollProjectTelemetryGate.Shared;
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction == LspMessageDirection.Receive &&
            string.Equals(message.Body?["method"]?.Value<string>(), CustomLspMethodNames.ReqnrollProjectDetected,
                System.StringComparison.Ordinal))
        {
            _gate.Open();
        }

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }
}
