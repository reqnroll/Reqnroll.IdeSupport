#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Keeps the VS C# step code lenses in sync with the server's binding registry via
/// <c>reqnroll/refreshCodeLens</c> — invalidates <em>all</em> tracked lenses, so a <c>.cs</c>
/// file that was the foreground editor before the server was ready picks up its usage counts
/// without the user having to switch tabs. Invalidation re-calls
/// <see cref="StepCodeLens.GetLabelAsync"/> with fresh data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acts on incremental refreshes too</b> (issue #343), so editing a binding's expression or
/// <c>[Scope]</c> repaints the <c>.cs</c> lens once the edit settles, rather than staying stale
/// until the next build. This was gated to <c>isFullReplacement</c>-only for a long time because
/// the VS SDK's <c>CodeLens.Invalidate()</c> was root-caused (by decompiling VS 18) as the trigger
/// for VS.Extensibility reactivating <c>ReqnrollLanguageClient</c>, forcing a second
/// <c>CreateServerConnectionAsync</c> on the same session — issue #156. That reconnect is now
/// survivable rather than fatal: #310 hands out a fresh VS-facing pipe per call instead of the
/// shared cached one, #399 stopped a peer session's late <c>shutdown</c> response being
/// misdelivered to the new connection (which VS's JsonRpc treats as a fatal protocol violation),
/// and #402 drops late responses to cancelled owned requests. #156's link 2 is still unidentified,
/// so the reconnect itself has not been eliminated — only made safe.
/// </para>
/// <para>
/// <b>Why the added cost is acceptable.</b> The incremental signal is not per-keystroke: the server
/// only publishes it when a Roslyn patch actually changed a binding's matched expression (a
/// method-body or comment edit never reaches it), and it is debounced server-side so a burst of
/// edits collapses into one notification after they settle — see
/// <c>BindingRegistryChangedHandler</c>/<c>FeatureRescanDebouncer</c>. The server process and its
/// binding registry are unaffected by the reconnect; only the local VS-facing relay pipe is rebuilt
/// (see <c>LspServerConnectionService</c>).
/// </para>
/// <para>
/// <b>Runaway guard.</b> Because an invalidation can itself provoke a reconnect, a pathological
/// feedback loop (reconnect → server re-publishes a refresh → invalidate → reconnect) would
/// otherwise be unbounded and would present as a hung IDE. Invalidations are therefore coalesced
/// over <see cref="DebounceWindowMs"/> and rate-limited to <see cref="MaxInvalidationsPerWindow"/>
/// per <see cref="RateWindowMs"/>; exceeding that logs a warning and suppresses further
/// invalidation until the window rolls over, degrading to the old build-only behavior instead of
/// spinning. No such loop has been observed — full-replacement invalidation has shipped for a long
/// time without one — but the failure mode is severe enough to bound explicitly.
/// </para>
/// </remarks>
internal sealed class CodeLensRefreshInterceptor : ILspMessageInterceptor, IDisposable
{
    // Coalesce bursts: the server debounces already, but several projects in one solution can each
    // publish their own refresh for a single user edit.
    private const int DebounceWindowMs = 400;

    // Runaway-loop bounds — deliberately generous, so only a genuine feedback loop trips them.
    private const int MaxInvalidationsPerWindow = 12;
    private const int RateWindowMs = 10_000;

    private readonly StepCodeLensState _state;
    private readonly ILogger<CodeLensRefreshInterceptor> _logger;
    private readonly Action _invalidate;

    private readonly object _gate = new();
    private Timer? _debounceTimer;
    private int _invalidationsThisWindow;
    private DateTime _windowStartedUtc = DateTime.UtcNow;
    private bool _suppressedWarningLogged;
    private bool _disposed;

    /// <summary>Creates the interceptor over the shared step-code-lens state.</summary>
    /// <param name="invalidateOverride">
    /// Replaces the UI-thread invalidation dispatch. Exists so the debounce and rate-guard
    /// bookkeeping — which is ordinary logic — can be tested without a VS host; production callers
    /// omit it and get <see cref="InvalidateAllOnUiThread"/>.
    /// </param>
    public CodeLensRefreshInterceptor(
        StepCodeLensState state,
        ILogger<CodeLensRefreshInterceptor> logger,
        Action? invalidateOverride = null)
    {
        _state      = state;
        _logger     = logger;
        _invalidate = invalidateOverride ?? InvalidateAllOnUiThread;
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

                // Both kinds now invalidate the VS.Extensibility (.cs) lenses — see the class
                // remarks for the #156 reconnect trade-off and the guards around it.
                ScheduleInvalidation(isFullReplacement);

                // The hook-match-count lens on .feature files (issue #372) is a separate, classic
                // (Microsoft.VisualStudio.Language.CodeLens) mechanism — its refresh is a plain
                // ITagger<T>.TagsChanged event, not VS.Extensibility's CodeLens.Invalidate(), so it
                // never provokes a reconnect and needs neither the debounce nor the rate guard.
                HookCodeLensRedirect.InvalidateAll();
            }
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    /// <summary>
    /// Coalesces refresh signals over <see cref="DebounceWindowMs"/> and fires one invalidation,
    /// subject to the runaway guard described in the class remarks.
    /// </summary>
    private void ScheduleInvalidation(bool isFullReplacement)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _logger.LogInformation(
                "CodeLensRefreshInterceptor: queued a {RefreshKind} refresh for the C# lenses.",
                isFullReplacement ? "full-replacement" : "incremental");

            _debounceTimer ??= new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(DebounceWindowMs, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var now = DateTime.UtcNow;
            if ((now - _windowStartedUtc).TotalMilliseconds >= RateWindowMs)
            {
                _windowStartedUtc         = now;
                _invalidationsThisWindow  = 0;
                _suppressedWarningLogged  = false;
            }

            if (_invalidationsThisWindow >= MaxInvalidationsPerWindow)
            {
                if (!_suppressedWarningLogged)
                {
                    _suppressedWarningLogged = true;
                    _logger.LogWarning(
                        "CodeLensRefreshInterceptor: more than {Max} CodeLens invalidations in {WindowMs}ms — " +
                        "suppressing further invalidation until the window rolls over. This suggests a " +
                        "refresh/reconnect feedback loop (issue #156); C# lenses will stay stale until the next build.",
                        MaxInvalidationsPerWindow, RateWindowMs);
                }
                return;
            }

            _invalidationsThisWindow++;
        }

        _invalidate();
        _logger.LogInformation("CodeLensRefreshInterceptor: invalidated all tracked C# lenses.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
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
