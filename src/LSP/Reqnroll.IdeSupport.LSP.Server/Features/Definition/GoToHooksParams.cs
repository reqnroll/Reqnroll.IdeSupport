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
}
