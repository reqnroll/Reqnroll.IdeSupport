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
    /// <summary>Delegate set by the Extension project: <c>(fileUri, ct) → every resolved Run target for that .feature file</c>.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<RunTestTargetEntry>>>? GetTargetsAsync { get; set; }

    /// <summary>The shared tagger registry every <see cref="LineKeyedCodeLensTagger{TEntry}"/> for this feature registers itself with.</summary>
    internal static readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<RunTestTargetEntry>> TaggerRegistry =
        new(tagger => tagger.RequestRefresh());

    /// <summary>Requests a re-pull of Run targets for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public static void InvalidateFile(string fileUri) => TaggerRegistry.InvalidateFile(fileUri);

    /// <summary>Requests a re-pull of Run targets for every tracked <c>.feature</c> file. Safe to call from any thread.</summary>
    public static void InvalidateAll() => TaggerRegistry.InvalidateAll();
}
