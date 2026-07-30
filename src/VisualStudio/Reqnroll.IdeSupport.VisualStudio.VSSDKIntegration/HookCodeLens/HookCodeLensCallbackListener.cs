#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;

namespace Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

/// <summary>
/// In-process (<c>devenv.exe</c>) callback target for the out-of-process hook-match-count CodeLens
/// data point provider (issue #372). <see cref="HookCodeLensDataPointProvider"/>/<see cref="HookCodeLensDataPoint"/>
/// run in the CodeLens ServiceHub host process, not <c>devenv.exe</c> — confirmed live by checking
/// the PID that invoked them against <c>tasklist</c> (<c>ServiceHub.Host.netfx.Any</c>). A static
/// bridge populated by <c>ReqnrollLanguageClient</c> (like <see cref="HookCodeLensRedirect"/>, which
/// this class delegates to) is invisible across that process boundary — each process has its own
/// copy of static state. <see cref="ICodeLensCallbackService"/>/<see cref="ICodeLensCallbackListener"/>
/// is the SDK's purpose-built JSON-RPC mechanism for an OOP CodeLens component to call back into VS;
/// this listener is the devenv-side target, exported as an ordinary MEF part so it composes into the
/// same in-proc container <see cref="HookCodeLensTaggerProvider"/> already lives in (where
/// <see cref="HookCodeLensRedirect"/> is actually populated).
/// </summary>
/// <remarks>
/// <c>[ContentType]</c> is required, not decorative: the devenv-side bridge
/// (<c>Microsoft.VisualStudio.CodeLens.Proxy.CodeLensHubClient</c>, decompiled from
/// <c>Microsoft.VisualStudio.Editor.Implementation.dll</c> while debugging issue #372) imports
/// listeners as <c>Lazy&lt;ICodeLensCallbackListener, IDeferrableContentTypeMetadata&gt;</c> and only
/// calls <c>AddLocalRpcTarget</c> for listeners whose declared content types match the connection's —
/// otherwise the listener is composed but never wired to the RPC channel, and every callback fails
/// with <c>RemoteMethodNotFoundException</c> even though the method genuinely exists.
/// </remarks>
[Export(typeof(ICodeLensCallbackListener))]
[ContentType("reqnroll-gherkin")]
public sealed class HookCodeLensCallbackListener : ICodeLensCallbackListener
{
    public const string GetLensesMethod      = "Reqnroll.HookCodeLens.GetLenses";
    public const string GetHookDetailsMethod = "Reqnroll.HookCodeLens.GetHookDetails";

    [JsonRpcMethod(GetLensesMethod)]
    public async Task<IReadOnlyList<HookFeatureLensEntry>> GetLensesAsync(string fileUri, CancellationToken cancellationToken)
    {
        var fetch = HookCodeLensRedirect.GetLensesAsync;
        return fetch is null
            ? Array.Empty<HookFeatureLensEntry>()
            : await fetch(fileUri, cancellationToken).ConfigureAwait(false);
    }

    [JsonRpcMethod(GetHookDetailsMethod)]
    public async Task<IReadOnlyList<HookDetailEntry>> GetHookDetailsAsync(string fileUri, int navLine, int navChar, bool ownLevelOnly, CancellationToken cancellationToken)
    {
        var fetch = HookCodeLensRedirect.GetHookDetailsAsync;
        return fetch is null
            ? Array.Empty<HookDetailEntry>()
            : await fetch(fileUri, navLine, navChar, ownLevelOnly, cancellationToken).ConfigureAwait(false);
    }
}
