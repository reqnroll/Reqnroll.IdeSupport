#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Handles the custom <c>reqnroll/testOutcomes/registerRun</c> request: the IDE's runsettings-injection
/// service calls this once per Test Explorer execution request, in place of what used to be an in-proc
/// call to a VS-only <c>TestOutcomeListener</c>.
/// </summary>
public sealed class RegisterTestRunHandler
{
    private readonly TestOutcomeTcpListener _listener;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    public RegisterTestRunHandler(TestOutcomeTcpListener listener, IIdeSupportLogger logger, IOperationDurationRecorder? recorder = null)
    {
        _listener = listener;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>reqnroll/testOutcomes/registerRun</c> request.</summary>
    public Task<RegisterTestRunResponse> HandleAsync(RegisterTestRunParams request, CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(LspMethodNames.ReqnrollRegisterTestRun);

        var registration = _listener.RegisterRun();
        if (registration is null)
        {
            _logger.LogWarning($"{nameof(RegisterTestRunHandler)}: could not start the loopback listener; returning Success=false.");
            return Task.FromResult(new RegisterTestRunResponse { Success = false });
        }

        return Task.FromResult(new RegisterTestRunResponse
        {
            Success = true,
            RunId = registration.RunId,
            Endpoint = registration.Endpoint,
        });
    }
}
