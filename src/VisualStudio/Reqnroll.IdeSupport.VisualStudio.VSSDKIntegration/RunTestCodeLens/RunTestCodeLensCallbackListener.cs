#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// In-process (<c>devenv.exe</c>) callback target for the out-of-process Run CodeLens data point
/// provider (design doc §5/§6, issue #262). Mirrors <c>HookCodeLensCallbackListener</c> exactly —
/// see its remarks for why <c>[ContentType]</c> is required (not decorative) and why a static
/// bridge alone can't reach across the OOP ServiceHub process boundary.
/// </summary>
[Export(typeof(ICodeLensCallbackListener))]
[ContentType("Gherkin")]
public sealed class RunTestCodeLensCallbackListener : ICodeLensCallbackListener
{
    public const string GetTargetsMethod = "Reqnroll.RunTestCodeLens.GetTargets";

    [JsonRpcMethod(GetTargetsMethod)]
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsAsync(string fileUri, CancellationToken cancellationToken)
    {
        var fetch = RunTestCodeLensRedirect.GetTargetsAsync;
        return fetch is null
            ? Array.Empty<RunTestTargetEntry>()
            : await fetch(fileUri, cancellationToken).ConfigureAwait(false);
    }
}
