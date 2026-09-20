#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Watches the server's <c>reqnroll/testOutcomes/changed</c> push (LSP-server outcome pipeline
/// refactor) and forwards it to <see cref="RunTestCodeLensRedirect.NotifyOutcomesChanged"/>, which
/// versions the Run CodeLens descriptors and refreshes every open <c>.feature</c> file's tagger — the
/// same effect the old in-proc <c>TestOutcomeListener</c> had when it called that method directly.
/// </summary>
/// <remarks>
/// No debounce/rate-limit here, unlike <see cref="CodeLensRefreshInterceptor"/>: this notification
/// doesn't call VS.Extensibility's <c>CodeLens.Invalidate()</c> (the classic VSSDK tagger it drives
/// has no equivalent reconnect risk — see issue #156), and the server already throttles how often it
/// sends this notification (<c>TestOutcomeTcpListener.ScheduleRefresh</c>), so there is nothing left
/// to coalesce on this side.
/// </remarks>
internal sealed class TestOutcomesChangedInterceptor : ILspMessageInterceptor
{
    private readonly ILogger<TestOutcomesChangedInterceptor> _logger;

    public TestOutcomesChangedInterceptor(ILogger<TestOutcomesChangedInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var method = message.Body?["method"]?.Value<string>();
        if (string.Equals(method, ReqnrollMethodNames.TestOutcomesChanged, System.StringComparison.Ordinal))
        {
            _logger.LogDebug("TestOutcomesChangedInterceptor: outcomes changed; notifying Run CodeLens.");
            RunTestCodeLensRedirect.NotifyOutcomesChanged();
        }

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }
}
