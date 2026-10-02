using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Runs one direction's interceptor list over a message (issue #587, step 3).
/// </summary>
/// <remarks>
/// Extracted because both pumps and both injection methods need it, and after step 3 the pumps live
/// in their own types. It also gives the fault-tolerance rule below a test target that does not
/// require a running pump.
/// </remarks>
internal sealed class InterceptorPipeline
{
    private readonly IReadOnlyList<ILspMessageInterceptor> _interceptors;
    private readonly ILogger _logger;

    /// <summary>Creates a pipeline over <paramref name="interceptors"/>, applied in order.</summary>
    public InterceptorPipeline(IReadOnlyList<ILspMessageInterceptor> interceptors, ILogger logger)
    {
        _interceptors = interceptors ?? throw new ArgumentNullException(nameof(interceptors));
        _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Offers <paramref name="message"/> to each interceptor until one consumes it.
    /// </summary>
    /// <returns>
    /// <see cref="LspInterceptorResult.Consume"/> if any interceptor consumed the message (the
    /// caller must not forward it), otherwise <see cref="LspInterceptorResult.PassThrough"/>.
    /// </returns>
    /// <remarks>
    /// An interceptor that throws is logged and skipped. Interceptors are observers of a live
    /// VS ↔ server connection — a bug in one must degrade to "this interceptor did not see this
    /// message", never sever the pipe.
    /// </remarks>
    public async Task<LspInterceptorResult> RunAsync(LspMessage message, CancellationToken cancellationToken)
    {
        foreach (var interceptor in _interceptors)
        {
            try
            {
                var result = await interceptor.InterceptAsync(message, cancellationToken).ConfigureAwait(false);
                if (result == LspInterceptorResult.Consume)
                    return LspInterceptorResult.Consume;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "InterceptorPipeline: interceptor {InterceptorType} threw.",
                    interceptor.GetType().Name);
            }
        }

        return LspInterceptorResult.PassThrough;
    }
}
