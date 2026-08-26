#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// Static bridge that the Extension project populates so the classic (VSSDK) Run CodeLens
/// components — which have no reference to <c>LspInterceptingPipe</c> and run via the classic
/// <c>Microsoft.VisualStudio.Language.CodeLens</c> API — can reach the LSP server (design doc
/// §5/§6 VS leg, issue #262). Mirrors <c>HookCodeLensRedirect</c>.
/// </summary>
/// <remarks>
/// Set by <c>ReqnrollLanguageClient</c> once the server connection is established; cleared on
/// dispose. Safe to call from any thread. Tagger tracking/invalidation itself lives in the shared
/// <see cref="WeakTaggerRegistry{TTagger}"/> (issue #262 follow-up — this class used to hold its own
/// copy of that registry, near-identical to <c>HookCodeLensRedirect</c>'s).
/// </remarks>
public static class RunTestCodeLensRedirect
{
    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, line, ct) → the resolved Run target(s)
    /// for exactly that line</c> (issue #495) — called by the out-of-process
    /// <see cref="RunTestCodeLensDataPoint"/>'s callback listener, one line at a time, instead of
    /// resolving the whole file and filtering.
    /// </summary>
    public static Func<string, int, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>>? GetTargetsForLineAsync { get; set; }

    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, ct) → every Run-lens tag placement for
    /// that .feature file</c> (issue #495) — symbol-tree only, no <c>resolveTestTargets</c> calls.
    /// Called in-process by <see cref="RunTestCodeLensTaggerProvider"/> to know which lines get a
    /// tag; the actual resolution happens lazily via <see cref="GetTargetsForLineAsync"/> once a
    /// line's own data point is created.
    /// </summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<RunTestLensLocation>>>? GetTagLocationsAsync { get; set; }

    /// <summary>The shared tagger registry every <see cref="LineKeyedCodeLensTagger{TEntry}"/> for this feature registers itself with.</summary>
    internal static readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<RunTestLensLocation>> TaggerRegistry =
        new(tagger => tagger.RequestRefresh());

    /// <summary>
    /// Set by the Extension project alongside <see cref="GetTargetsAsync"/> — lets this class
    /// invalidate the Extension's own shared-result cache (issue #262 follow-up: multiple
    /// concurrent callers, the tagger and every visible line's own out-of-process CodeLens data
    /// point, share one computation per file) without this VSSDKIntegration project needing a
    /// reference to that cache's type.
    /// </summary>
    public static Action<string>? InvalidateCachedFile { get; set; }

    /// <summary>Set by the Extension project alongside <see cref="GetTargetsAsync"/> — see <see cref="InvalidateCachedFile"/>.</summary>
    public static Action? InvalidateAllCached { get; set; }

    /// <summary>Requests a re-pull of Run targets for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public static void InvalidateFile(string fileUri)
    {
        InvalidateCachedFile?.Invoke(fileUri);
        TaggerRegistry.InvalidateFile(fileUri);
    }

    /// <summary>Requests a re-pull of Run targets for every tracked <c>.feature</c> file. Safe to call from any thread.</summary>
    public static void InvalidateAll()
    {
        InvalidateAllCached?.Invoke();
        TaggerRegistry.InvalidateAll();
    }
}
