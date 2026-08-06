#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;

/// <summary>
/// Sends a custom <c>reqnroll/goToMatchingScenarios</c> request to the LSP server and maps the
/// result to a <see cref="GoToMatchingScenariosResult"/> (issue #373's hook-match-count CodeLens
/// click action) — the inverse of <see cref="GoToHooks.GoToHooksService"/>.
/// </summary>
internal sealed class GoToMatchingScenariosService
{
    private const string RequestMethod = "reqnroll/goToMatchingScenarios";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<GoToMatchingScenariosService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public GoToMatchingScenariosService(LspInterceptingPipe pipe, ILogger<GoToMatchingScenariosService> logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for scenarios matched by the hook binding at
    /// <paramref name="line0"/> / <paramref name="char0"/> in <paramref name="fileUri"/> (all
    /// 0-based) — the exact attribute location the lens was rendered at.
    /// </summary>
    public async Task<GoToMatchingScenariosResult> GoToMatchingScenariosAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0);

        _logger.LogInformation(
            "GoToMatchingScenariosService: querying {RequestMethod} at {FileUri}:{Line0}:{Char0}", RequestMethod, fileUri, line0, char0);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "GoToMatchingScenariosService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        var mapped = MapResult(result);
        _logger.LogInformation("GoToMatchingScenariosService: {ScenarioCount} scenario(s) returned", mapped.Scenarios.Count);
        return mapped;
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/goToMatchingScenarios</c> JSON result to a
    /// <see cref="GoToMatchingScenariosResult"/>. Separated from transport so it can be
    /// unit-tested. A <c>null</c>, non-object, or missing-<c>scenarios</c> result yields
    /// <see cref="GoToMatchingScenariosResult.Empty"/>.
    /// </summary>
    internal static GoToMatchingScenariosResult MapResult(JToken? result)
    {
        if (result is null || result.Type == JTokenType.Null)
            return GoToMatchingScenariosResult.Empty;

        if (result is JObject obj)
        {
            var scenariosArray = obj["scenarios"] as JArray ?? new JArray();
            return new GoToMatchingScenariosResult(ParseScenarios(scenariosArray));
        }

        return GoToMatchingScenariosResult.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0) =>
        LspParamsBuilder.TextDocumentPosition(fileUri, line0, char0);

    private static IReadOnlyList<MatchingScenarioLocation> ParseScenarios(JArray array)
    {
        var result = new List<MatchingScenarioLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var startLine    = obj["startLine"]?.Value<int>()     ?? 0;
            var startChar    = obj["startChar"]?.Value<int>()     ?? 0;
            var scenarioName = obj["scenarioName"]?.Value<string>() ?? "";
            var isOutline    = obj["isOutline"]?.Value<bool>()    ?? false;

            result.Add(new MatchingScenarioLocation(uri, startLine, startChar, scenarioName, isOutline));
        }
        return result;
    }
}
