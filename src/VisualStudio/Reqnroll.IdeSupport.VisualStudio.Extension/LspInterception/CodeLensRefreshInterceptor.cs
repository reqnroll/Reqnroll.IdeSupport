#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Keeps the VS C# step code lenses in sync with the server's binding registry via
/// <c>reqnroll/refreshCodeLens</c>, pushed by the server after a full binding-registry replacement
/// (e.g. startup connector discovery) — invalidates <em>all</em> tracked lenses, so a <c>.cs</c>
/// file that was the foreground editor before the server was ready picks up its usage counts
/// without the user having to switch tabs. Invalidation re-calls
/// <see cref="StepCodeLens.GetLabelAsync"/> with fresh data.
/// </summary>
/// <remarks>
/// Only acts when the notification's <c>isFullReplacement</c> is <see langword="true"/> (issue
/// #156/#318): calling the VS SDK's <c>CodeLens.Invalidate()</c> was root-caused as the trigger for
/// VS.Extensibility reactivating <c>ReqnrollLanguageClient</c>, forcing a second
/// <c>CreateServerConnectionAsync</c> call on the same session. #310 made that survivable (a fresh
/// pipe per call instead of the shared cached one), but the reconnect churn itself is still
/// unnecessary and risks a transiently unresponsive client mid-swap. A per-<c>.cs</c>-edit trigger
/// used to live here too and was disabled for the same reason — but the server also pushes this same
/// notification, with <c>isFullReplacement=false</c>, for incremental Roslyn patches and
/// <c>.feature</c>-edit usage-count changes (see <c>BindingRegistryChangedHandler</c> and
/// <c>CodeLensRefreshHandler</c>), which would reproduce the identical reconnect churn on every
/// settled edit if acted on. Step-usage counts on an already-open <c>.cs</c> file's lenses go stale
/// until the next full refresh (e.g. after a build) instead of repainting live per edit. TODO(#318):
/// act on incremental refreshes too once #156's root cause is understood or a confirmed-safe
/// reconnect path exists.
/// </remarks>
internal sealed class CodeLensRefreshInterceptor : ILspMessageInterceptor
{
    private readonly StepCodeLensState _state;
    private readonly ILogger<CodeLensRefreshInterceptor> _logger;

    /// <summary>Creates the interceptor over the shared step-code-lens state.</summary>
    public CodeLensRefreshInterceptor(StepCodeLensState state, ILogger<CodeLensRefreshInterceptor> logger)
    {
        _state  = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(
        LspMessage message,
        CancellationToken cancellationToken)
    {
        var body = message.Body;
        if (body is null)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var method = body["method"]?.Value<string>();
        if (method is null)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        // Server→client: full binding-registry replacement completed. Re-pull every tracked lens,
        // because lenses for an already-open .cs file were rendered before the server had counts.
        if (message.Direction == LspMessageDirection.Receive)
        {
            if (string.Equals(method, "reqnroll/refreshCodeLens", StringComparison.Ordinal))
            {
                var isFullReplacement = body["params"]?["isFullReplacement"]?.Value<bool>() ?? false;
                if (isFullReplacement)
                {
                    InvalidateAllOnUiThread();
                    _logger.LogInformation(
                        "CodeLensRefreshInterceptor: refreshed all tracked lenses on full-replacement server signal.");
                }
                else
                {
                    _logger.LogInformation(
                        "CodeLensRefreshInterceptor: skipped incremental refresh signal to avoid reconnect churn (#156/#318).");
                }
            }
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }

        // TODO(#318): per-.cs-edit invalidation (textDocument/didChange -> CodeLens.Invalidate() for
        // just that file's lenses) is disabled — see the class remarks and issue #156/#318.
        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    /// <summary>Invalidates every tracked lens on the UI thread.</summary>
    /// <remarks>
    /// Must run on the UI thread — <c>CodeLens.Invalidate()</c> in the VS Extensibility SDK sets an
    /// internal dirty flag that only takes effect when called from the main thread.
    /// </remarks>
    private void InvalidateAllOnUiThread()
    {
        var jtf = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory;
        _ = jtf.RunAsync(async () =>
        {
            await jtf.SwitchToMainThreadAsync();
            _state.InvalidateAllTrackedLenses();
        });
    }
}
