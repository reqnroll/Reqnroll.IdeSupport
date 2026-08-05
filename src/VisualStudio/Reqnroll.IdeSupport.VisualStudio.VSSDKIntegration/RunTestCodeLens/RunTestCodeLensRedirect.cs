#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Static bridge that the Extension project populates so the classic (VSSDK) Run CodeLens
/// components — which have no reference to <c>LspInterceptingPipe</c> and run via the classic
/// <c>Microsoft.VisualStudio.Language.CodeLens</c> API — can reach the LSP server (design doc
/// §5/§6 VS leg, issue #262). Mirrors <c>HookCodeLensRedirect</c> exactly, including the
/// weak-tagger-invalidation registry.
/// </summary>
/// <remarks>Set by <c>ReqnrollLanguageClient</c> once the server connection is established; cleared on dispose. Safe to call from any thread.</remarks>
public static class RunTestCodeLensRedirect
{
    /// <summary>Delegate set by the Extension project: <c>(fileUri, ct) → every resolved Run target for that .feature file</c>.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>>? GetTargetsAsync { get; set; }

    // ── Tagger invalidation (mirrors HookCodeLensRedirect's registry) ──────────

    private static readonly ConcurrentDictionary<string, List<WeakReference<RunTestCodeLensTagger>>> _taggersByFile
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    internal static void RegisterTagger(RunTestCodeLensTagger tagger, string fileUri)
    {
        lock (_lock)
        {
            var list = _taggersByFile.GetOrAdd(fileUri, _ => new List<WeakReference<RunTestCodeLensTagger>>());
            list.Add(new WeakReference<RunTestCodeLensTagger>(tagger));
        }
    }

    internal static void UnregisterTagger(RunTestCodeLensTagger tagger, string fileUri)
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

    /// <summary>Requests a re-pull of Run targets for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public static void InvalidateFile(string fileUri)
    {
        lock (_lock)
        {
            if (!_taggersByFile.TryGetValue(fileUri, out var list))
                return;

            var alive = new List<WeakReference<RunTestCodeLensTagger>>(list.Count);
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

    /// <summary>Requests a re-pull of Run targets for every tracked <c>.feature</c> file. Safe to call from any thread.</summary>
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
