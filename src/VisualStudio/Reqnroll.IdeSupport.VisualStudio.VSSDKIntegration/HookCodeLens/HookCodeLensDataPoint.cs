#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Threading;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// A single hook-match-count CodeLens data point (issue #372): resolves the label ("N hooks") and,
/// on request, the Details popup listing each matching hook with a navigation command.
/// </summary>
/// <remarks>
/// Runs out-of-process (see <see cref="HookCodeLensDataPointProvider"/>'s remarks) — reaches the LSP
/// bridge via <see cref="ICodeLensCallbackService"/> calling back into
/// <see cref="HookCodeLensCallbackListener"/>, not the static <see cref="HookCodeLensRedirect"/>.
/// </remarks>
/// <remarks>
/// <b>Deadlock avoidance (found live via a captured process dump):</b> when the user clicks a lens,
/// VS's own <c>CodeLensDataPointPresenter.OnShowDetailsExecuted</c> blocks the UI thread synchronously
/// on <see cref="GetDetailsAsync"/> via <c>JoinableTask.CompleteOnCurrentThread</c> — critically, using
/// a <c>NoMessagePumpSyncContext</c>, i.e. <i>without</i> pumping the WPF message queue while it waits.
/// <see cref="ICodeLensCallbackService"/> calls are dispatched back into <c>devenv.exe</c> via
/// StreamJsonRpc, which marshals onto the UI thread's captured <c>SynchronizationContext</c> — a
/// dispatch that itself needs the message queue pumped to run. If <see cref="GetDetailsAsync"/> made
/// its own callback round-trip at that point, the two would deadlock: the click blocks the UI thread
/// waiting for the callback; the callback needs the (blocked, non-pumping) UI thread to even start.
/// <see cref="GetDataAsync"/> always runs first — the lens must render before it's clickable — and
/// does so on a normal async path (never inside the blocking wait), so it pre-fetches and caches the
/// hook-detail list here. <see cref="GetDetailsAsync"/> then returns the cached result with no further
/// cross-process call, so there is nothing left to deadlock on. The only remaining round-trip on the
/// click path is <see cref="GetDataAsync"/>'s own — a defensive fallback for the (never expected in
/// practice) case <see cref="GetDetailsAsync"/> is invoked before <see cref="GetDataAsync"/> ever ran.
/// </remarks>
internal sealed class HookCodeLensDataPoint : IAsyncCodeLensDataPoint
{
    private readonly ICodeLensCallbackService _callbackService;
    private readonly string _fileUri;
    private readonly int    _line;
    private readonly bool   _isStepHooksLens;

    private IReadOnlyList<HookDetailEntry>? _cachedHooks;

    /// <param name="isStepHooksLens">
    /// Which of the two lens kinds this data point renders. Supplied by the owning provider, not
    /// decoded from the descriptor: a tag marks a whole line, and both providers share it (see
    /// <see cref="HookElementDescription"/>'s remarks).
    /// </param>
    public HookCodeLensDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService, string fileUri, int line, bool isStepHooksLens)
    {
        Descriptor       = descriptor;
        _callbackService = callbackService;
        _fileUri         = fileUri;
        _line            = line;
        _isStepHooksLens = isStepHooksLens;
    }

    /// <inheritdoc />
    public CodeLensDescriptor Descriptor { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Never raised in this first pass — a hook-match count can only change label-side (the tag's
    /// own position/text doesn't move), and the classic API gives data points no disposal hook to
    /// safely unsubscribe from a shared invalidation source. Labels still refresh naturally
    /// whenever CodeLens re-creates data points (e.g. on a document edit).
    /// </remarks>
    public event AsyncEventHandler? InvalidatedAsync { add { } remove { } }

    /// <inheritdoc />
    public async Task<CodeLensDataPointDescriptor> GetDataAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        IReadOnlyList<HookFeatureLensEntry> lenses;
        try
        {
            lenses = await _callbackService
                .InvokeAsync<IReadOnlyList<HookFeatureLensEntry>>(this, HookCodeLensCallbackListener.GetLensesMethod, new object[] { _fileUri }, token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            lenses = Array.Empty<HookFeatureLensEntry>();
        }

        // Both providers get a data point for every tag, because a tag marks a line rather than an
        // individual lens (see HookElementDescription's remarks) and neither provider can know in
        // advance whether its own kind is present. Pick out this data point's kind here; when the
        // line has no lens of this kind, the empty Description leaves this provider contributing no
        // indicator while the other one still renders normally.
        var entry = lenses.FirstOrDefault(e => e.Line == _line && e.AlwaysShowPicker == _isStepHooksLens);
        if (entry is null)
        {
            _cachedHooks = Array.Empty<HookDetailEntry>();
            return new CodeLensDataPointDescriptor { Description = string.Empty };
        }

        // Pre-fetch and cache the Details-popup content now, while still on a normal async path —
        // see this type's remarks for why GetDetailsAsync must not make its own callback round-trip.
        // The nav target comes from the resolved entry: the two lens kinds on a Scenario: line point
        // at different places (own-level at the Scenario: tag, step-hooks at its first step), and the
        // descriptor no longer carries per-lens nav info.
        _cachedHooks = await FetchHooksAsync(entry.NavLine, entry.NavChar, entry.OwnLevelOnly, token).ConfigureAwait(false);

        return new CodeLensDataPointDescriptor { Description = entry.Title };
    }

    /// <inheritdoc />
    public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        // Always populated by GetDataAsync (see this type's remarks), which must run first for the
        // lens to be clickable at all — so on the normal click path this returns with no further
        // cross-process call, which is what keeps the UI thread from deadlocking. An empty list is
        // the correct answer when this line has no lens of this data point's kind.
        var hooks = _cachedHooks ?? Array.Empty<HookDetailEntry>();

        var headers = new[]
        {
            new CodeLensDetailHeaderDescriptor { UniqueName = "hookType",   DisplayName = "Hook",  Width = 0.4 },
            new CodeLensDetailHeaderDescriptor { UniqueName = "methodName", DisplayName = "Method", Width = 0.6 },
        };

        var entries = hooks.Select(h => new CodeLensDetailEntryDescriptor
        {
            Fields = new[]
            {
                new CodeLensDetailEntryField { Text = $"[{h.HookType}]" },
                new CodeLensDetailEntryField { Text = h.MethodName },
            },
            Tooltip = h.HookOrder != 0
                ? $"{h.MethodName} (Order: {h.HookOrder.ToString(CultureInfo.InvariantCulture)})"
                : h.MethodName,
            NavigationCommand = new CodeLensDetailEntryCommand
            {
                CommandSet = HookCodeLensCommandIds.CommandSet,
                CommandId  = HookCodeLensCommandIds.NavigateToHookCommandId,
            },
            NavigationCommandArgs = new object[] { $"{h.TargetUri}|{h.TargetLine}|{h.TargetChar}" },
        }).ToList();

        return new CodeLensDetailsDescriptor
        {
            Headers = headers,
            Entries = entries,
        };
    }

    private async Task<IReadOnlyList<HookDetailEntry>> FetchHooksAsync(int navLine, int navChar, bool ownLevelOnly, CancellationToken token)
    {
        try
        {
            return await _callbackService
                .InvokeAsync<IReadOnlyList<HookDetailEntry>>(this, HookCodeLensCallbackListener.GetHookDetailsMethod, new object[] { _fileUri, navLine, navChar, ownLevelOnly }, token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return Array.Empty<HookDetailEntry>();
        }
    }
}
