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
internal sealed class HookCodeLensDataPoint : IAsyncCodeLensDataPoint
{
    private readonly ICodeLensCallbackService _callbackService;
    private readonly string _fileUri;
    private readonly int    _line;
    private readonly int    _navLine;
    private readonly int    _navChar;
    private readonly bool   _ownLevelOnly;

    public HookCodeLensDataPoint(CodeLensDescriptor descriptor, ICodeLensCallbackService callbackService, string fileUri, int line, int navLine, int navChar, bool ownLevelOnly)
    {
        Descriptor       = descriptor;
        _callbackService = callbackService;
        _fileUri         = fileUri;
        _line            = line;
        _navLine         = navLine;
        _navChar         = navChar;
        _ownLevelOnly    = ownLevelOnly;
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

        var entry = lenses.FirstOrDefault(e => e.Line == _line);

        return new CodeLensDataPointDescriptor { Description = entry?.Title ?? string.Empty };
    }

    /// <inheritdoc />
    public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CodeLensDescriptorContext descriptorContext, CancellationToken token)
    {
        IReadOnlyList<HookDetailEntry> hooks;
        try
        {
            hooks = await _callbackService
                .InvokeAsync<IReadOnlyList<HookDetailEntry>>(this, HookCodeLensCallbackListener.GetHookDetailsMethod, new object[] { _fileUri, _navLine, _navChar, _ownLevelOnly }, token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            hooks = Array.Empty<HookDetailEntry>();
        }

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
}
