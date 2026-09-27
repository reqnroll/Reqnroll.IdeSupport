#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Request params for the custom <c>reqnroll/goToHooks</c> request. Extends the standard
/// position params with an optional flag set by the hook-count CodeLens (issue #269 follow-up)
/// so that clicking a lens shows exactly the hooks it counted, instead of the cumulative list
/// a manual "Go to Hooks" invocation from the cursor still returns.
/// </summary>
public sealed record GoToHooksParams : TextDocumentPositionParams
{
    /// <summary>
    /// When <see langword="true"/>, restricts results to hook types native to the resolved
    /// context level (see <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.HookMatching.GetOwnLevelHookTypes"/>)
    /// instead of the cumulative set that also includes enclosing Feature/Scenario hooks.
    /// Defaults to <see langword="false"/> for manual invocations (context menu, keybinding).
    /// </summary>
    [JsonProperty("ownLevelOnly")]
    public bool OwnLevelOnly { get; set; }

    /// <summary>
    /// When <see langword="true"/>, marks this request as the classic Visual Studio hook-match-count
    /// CodeLens's Details-popup prefetch (see <c>HookCodeLensDataPoint.GetDataAsync</c> in the VS
    /// extension project) rather than a user-initiated "Go to Hooks" navigation (issue #698). That
    /// data point re-uses this same request/handler to populate its popup on every CodeLens render —
    /// not only on a click — so without this flag every lens render logged/transmitted the same
    /// <c>"GoToHook command executed"</c> entry a real navigation does, making it look like the user
    /// was repeatedly invoking "Go to Hooks". Defaults to <see langword="false"/>: every other known
    /// caller (the context-menu/keybinding command, and the <c>reqnroll.goToHooks</c> client command
    /// VS Code/Rider invoke when a lens is actually clicked) is a genuine navigation and omits it.
    /// </summary>
    [JsonProperty("isCodeLensPrefetch")]
    public bool IsCodeLensPrefetch { get; set; }
}
