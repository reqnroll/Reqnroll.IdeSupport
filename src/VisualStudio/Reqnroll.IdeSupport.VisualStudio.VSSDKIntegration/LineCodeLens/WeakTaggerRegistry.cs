#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

/// <summary>
/// Tracks live classic-CodeLens taggers per file, weakly (so a closed buffer's tagger can be
/// collected), and lets a feature's static redirect bridge request a re-pull of server data for a
/// file (or every tracked file) without needing a live reference to the tagger itself. Shared by
/// every Reqnroll classic CodeLens feature (issue #372/#262) — extracted from what were two
/// near-identical copies of this exact registry embedded in <c>HookCodeLensRedirect</c>/
/// <c>RunTestCodeLensRedirect</c>.
/// </summary>
/// <remarks>
/// A classic <c>ICodeLensTag</c> has no re-tag/invalidate mechanism of its own; the standard pattern
/// is for the owning <c>ITagger</c> to raise <c>TagsChanged</c>, which asks the CodeLens host to
/// re-request tags for that span — <paramref name="requestRefresh"/> is how this registry asks a
/// tracked tagger to do that.
/// </remarks>
internal sealed class WeakTaggerRegistry<TTagger> where TTagger : class
{
    private readonly Action<TTagger> _requestRefresh;
    private readonly ConcurrentDictionary<string, List<WeakReference<TTagger>>> _taggersByFile =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public WeakTaggerRegistry(Action<TTagger> requestRefresh)
    {
        _requestRefresh = requestRefresh;
    }

    public void RegisterTagger(TTagger tagger, string fileUri)
    {
        lock (_lock)
        {
            var list = _taggersByFile.GetOrAdd(fileUri, _ => new List<WeakReference<TTagger>>());
            list.Add(new WeakReference<TTagger>(tagger));
        }
    }

    public void UnregisterTagger(TTagger tagger, string fileUri)
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

    /// <summary>Requests a re-pull of server data for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public void InvalidateFile(string fileUri)
    {
        lock (_lock)
        {
            if (!_taggersByFile.TryGetValue(fileUri, out var list))
                return;

            var alive = new List<WeakReference<TTagger>>(list.Count);
            foreach (var w in list)
            {
                if (w.TryGetTarget(out var tagger))
                {
                    _requestRefresh(tagger);
                    alive.Add(w);
                }
            }
            _taggersByFile[fileUri] = alive;
        }
    }

    /// <summary>Requests a re-pull of server data for every tracked file. Safe to call from any thread.</summary>
    public void InvalidateAll()
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
