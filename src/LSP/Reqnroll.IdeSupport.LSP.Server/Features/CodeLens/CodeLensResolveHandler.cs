using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Dispatches a <c>codeLens/resolve</c> request to whichever handler produced the lens, based on
/// the <c>"kind"</c> discriminator each handler embeds in <c>CodeLens.Data</c> (issue #471). Both
/// <see cref="StepCodeLensHandler"/> and <see cref="HookMatchCountCodeLensHandler"/> only ever
/// emit a lens with <c>Data</c> set when they've deferred its <c>Command</c> to resolve (non-VS
/// clients only, see their own remarks) — <see cref="HookCodeLensHandler"/>'s <c>.feature</c>-file
/// lenses are cheap enough to always compute eagerly and never carry <c>Data</c>, so they never
/// reach this dispatcher.
/// </summary>
public sealed class CodeLensResolveHandler
{
    private readonly StepCodeLensHandler          _stepHandler;
    private readonly HookMatchCountCodeLensHandler _hookMatchCountHandler;

    /// <summary>Initializes a new instance of the <see cref="CodeLensResolveHandler"/> class.</summary>
    public CodeLensResolveHandler(StepCodeLensHandler stepHandler, HookMatchCountCodeLensHandler hookMatchCountHandler)
    {
        _stepHandler           = stepHandler;
        _hookMatchCountHandler = hookMatchCountHandler;
    }

    /// <summary>Handles a <c>codeLens/resolve</c> request by routing to the originating handler's own resolve logic.</summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> ResolveAsync(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, CancellationToken cancellationToken)
    {
        var kind = (lens.Data as JObject)?["kind"]?.Value<string>();
        return kind switch
        {
            "stepUsage"       => _stepHandler.ResolveAsync(lens, cancellationToken),
            "hookMatchCount"  => _hookMatchCountHandler.ResolveAsync(lens, cancellationToken),
            _                 => Task.FromResult(lens)
        };
    }
}
