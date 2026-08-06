#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToHooks;

/// <summary>
/// Sends a custom <c>reqnroll/goToHooks</c> request to the LSP server and maps the result
/// to a <see cref="GoToHooksResult"/> (design doc: Hook Navigation / "Go to Hooks").
/// </summary>
/// <remarks>
/// Uses the custom <c>reqnroll/goToHooks</c> message rather than <c>textDocument/definition</c>
/// because Go to Step Definition already uses that message on step lines; the server cannot
/// distinguish "find step binding" from "find hooks" from position alone, and step-level hooks
/// (<c>[BeforeStep]</c> / <c>[AfterStep]</c>) would be unreachable via the shared message.
/// </remarks>
internal sealed class GoToHooksService
{
    private const string RequestMethod = "reqnroll/goToHooks";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<GoToHooksService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public GoToHooksService(LspInterceptingPipe pipe, ILogger<GoToHooksService> logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for applicable hooks at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public Task<GoToHooksResult> GoToHooksAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken) =>
        GoToHooksAsync(fileUri, line0, char0, ownLevelOnly: false, cancellationToken);

    /// <summary>
    /// Queries the LSP server for applicable hooks at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based), optionally restricted
    /// to hooks native to the resolved context level — set by the hook-match-count CodeLens
    /// (classic-CodeLens bridge, issue #372) so its Details popup shows exactly the hooks the lens
    /// counted, matching what <c>reqnroll.goToHooks</c>-invoking clients already do.
    /// </summary>
    public async Task<GoToHooksResult> GoToHooksAsync(
        string            fileUri,
        int               line0,
        int               char0,
        bool              ownLevelOnly,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0, ownLevelOnly);

        _logger.LogInformation(
            "GoToHooksService: querying {RequestMethod} at {FileUri}:{Line0}:{Char0}", RequestMethod, fileUri, line0, char0);
        _logger.LogInformation(
            "GoToHooksService: sending {RequestMethod} params={ParamsJson}", RequestMethod, paramsJson);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "GoToHooksService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        var mapped = MapResult(result);
        _logger.LogInformation("GoToHooksService: {HookCount} hook(s) returned", mapped.Hooks.Count);
        return mapped;
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/goToHooks</c> JSON result to a
    /// <see cref="GoToHooksResult"/>. Separated from transport so it can be unit-tested.
    /// A <c>null</c>, non-object, or missing-<c>hooks</c> result yields
    /// <see cref="GoToHooksResult.Empty"/>.
    /// </summary>
    internal static GoToHooksResult MapResult(JToken? result)
    {
        if (result is null || result.Type == JTokenType.Null)
            return GoToHooksResult.Empty;

        if (result is JObject obj)
        {
            var hooksArray = obj["hooks"] as JArray ?? new JArray();
            return new GoToHooksResult(ParseHooks(hooksArray));
        }

        return GoToHooksResult.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0, bool ownLevelOnly) =>
        new LspParamsBuilder()
            .AddTextDocument(fileUri)
            .AddPosition(line0, char0)
            .AddBool("ownLevelOnly", ownLevelOnly)
            .Build();

    private static IReadOnlyList<HookLocation> ParseHooks(JArray array)
    {
        var result = new List<HookLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri        = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var startLine  = obj["startLine"]?.Value<int>()  ?? 0;
            var startChar  = obj["startChar"]?.Value<int>()  ?? 0;
            var hookType   = obj["hookType"]?.Value<string>() ?? "";
            var hookOrder  = obj["hookOrder"]?.Value<int>()  ?? 10000;
            var methodName = obj["methodName"]?.Value<string>() ?? "";

            result.Add(new HookLocation(uri, startLine, startChar, hookType, hookOrder, methodName));
        }
        return result;
    }
}
