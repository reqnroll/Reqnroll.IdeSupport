#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// Static bridge that the Extension project populates so the classic (VSSDK) hook-match-count
/// CodeLens components — which have no reference to <c>LspInterceptingPipe</c> and run via the
/// classic <c>Microsoft.VisualStudio.Language.CodeLens</c> API rather than
/// <c>Microsoft.VisualStudio.Extensibility</c>'s <c>ICodeLensProvider</c> (see issue #372) — can
/// reach the LSP server.
/// </summary>
/// <remarks>
/// Set by <c>ReqnrollLanguageClient</c> once the server connection is established; cleared on
/// dispose. Mirrors <see cref="CommentToggleRedirect"/>/<see cref="NavigationBar.NavigationBarRedirect"/>.
/// Safe to call from any thread.
/// </remarks>
public static class HookCodeLensRedirect
{
    /// <summary>Delegate set by the Extension project: <c>(fileUri, ct) → hook-match lenses for that .feature file</c>.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<HookFeatureLensEntry>>>? GetLensesAsync { get; set; }

    /// <summary>Delegate set by the Extension project: <c>(fileUri, line0, char0, ownLevelOnly, ct) → the hooks a lens's Details popup should list</c>.</summary>
    public static Func<string, int, int, bool, CancellationToken, Task<IReadOnlyList<HookDetailEntry>>>? GetHookDetailsAsync { get; set; }

    // Navigation itself (opening/revealing a hook's definition on a Details-popup click) is handled
    // entirely in classic code — ReqnrollPluginPackage's IOleCommandTarget.Exec uses
    // VsShellUtilities.OpenDocument/IVsTextView.SetCaretPos directly — so it needs no LSP bridge
    // and isn't a delegate here.

    // ── Tagger invalidation (mirrors StepCodeLensState.RegisterLens/InvalidateLensesForFile) ──
    //
    // A classic ICodeLensTag has no re-tag/invalidate mechanism of its own; the standard pattern
    // is for the ITagger to raise TagsChanged, which asks the CodeLens host to re-request tags
    // for that span. We track live taggers per file (weakly, so a closed buffer's tagger can be
    // collected) and refresh them when the server signals a binding-registry change that may have
    // altered hook-match counts.

    private static readonly ConcurrentDictionary<string, List<WeakReference<HookCodeLensTagger>>> _taggersByFile
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    internal static void RegisterTagger(HookCodeLensTagger tagger, string fileUri)
    {
        lock (_lock)
        {
            var list = _taggersByFile.GetOrAdd(fileUri, _ => new List<WeakReference<HookCodeLensTagger>>());
            list.Add(new WeakReference<HookCodeLensTagger>(tagger));
        }
    }

    internal static void UnregisterTagger(HookCodeLensTagger tagger, string fileUri)
    {
        lock (_lock)
        {
            if (_taggersByFile.TryGetValue(fileUri, out var list))
            {
                list.RemoveAll(w => !w.TryGetTarget(out var t) || t == tagger);
                if (list.Count == 0)
                    _taggersByFile.TryRemove(fileUri, out _);
            }
        }
    }

    /// <summary>Requests a re-pull of hook-match lenses for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public static void InvalidateFile(string fileUri)
    {
        lock (_lock)
        {
            if (!_taggersByFile.TryGetValue(fileUri, out var list))
                return;

            var alive = new List<WeakReference<HookCodeLensTagger>>(list.Count);
            foreach (var w in list)
            {
                if (w.TryGetTarget(out var tagger))
                {
                    tagger.RequestRefresh();
                    alive.Add(w);
                }
            }
            _taggersByFile[fileUri] = alive;
        }
    }

    /// <summary>Requests a re-pull of hook-match lenses for every tracked <c>.feature</c> file. Safe to call from any thread.</summary>
    public static void InvalidateAll()
    {
        List<string> fileUris;
        lock (_lock)
        {
            fileUris = _taggersByFile.Keys.ToList();
        }

        foreach (var fileUri in fileUris)
            InvalidateFile(fileUri);
    }
}
