#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Request params for the custom <c>reqnroll/findHooks</c> request. Extends the standard
/// position params with an optional flag set by the hook-count CodeLens
/// so that clicking a lens shows exactly the hooks it counted, instead of the cumulative list
/// a manual "Go to Hooks" invocation from the cursor still returns.
/// </summary>
public sealed record FindHooksParams : TextDocumentPositionParams
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
    /// When <see langword="true"/>, hooks with no source file on this machine are included too, as
    /// entries with an empty <see cref="FindHookLocation.Uri"/>, <see cref="FindHookLocation.IsResolved"/>
    /// <see langword="false"/> and, when known, a <see cref="FindHookLocation.MethodIdentity"/> to
    /// navigate by. Only a client that can navigate by identity sets it; by default such hooks are
    /// left out, since a client expecting a URI on every entry cannot open them.
    /// </summary>
    [JsonProperty("includeUnresolved")]
    public bool IncludeUnresolved { get; set; }
}
