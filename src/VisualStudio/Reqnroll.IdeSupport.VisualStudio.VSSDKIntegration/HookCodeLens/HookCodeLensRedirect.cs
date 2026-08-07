#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

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
/// Safe to call from any thread. Tagger tracking/invalidation itself lives in the shared
/// <see cref="WeakTaggerRegistry{TTagger}"/> (issue #262 follow-up — this class used to hold its own
/// copy of that registry, near-identical to <c>RunTestCodeLensRedirect</c>'s).
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

    /// <summary>The shared tagger registry every <see cref="LineKeyedCodeLensTagger{TEntry}"/> for this feature registers itself with.</summary>
    internal static readonly WeakTaggerRegistry<LineKeyedCodeLensTagger<HookFeatureLensEntry>> TaggerRegistry =
        new(tagger => tagger.RequestRefresh());

    /// <summary>Requests a re-pull of hook-match lenses for <paramref name="fileUri"/>. Safe to call from any thread.</summary>
    public static void InvalidateFile(string fileUri) => TaggerRegistry.InvalidateFile(fileUri);

    /// <summary>Requests a re-pull of hook-match lenses for every tracked <c>.feature</c> file. Safe to call from any thread.</summary>
    public static void InvalidateAll() => TaggerRegistry.InvalidateAll();
}
