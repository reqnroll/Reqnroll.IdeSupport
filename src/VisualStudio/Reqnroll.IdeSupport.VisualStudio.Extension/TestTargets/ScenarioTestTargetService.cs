#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.TestTargets;

/// <summary>
/// Sends the custom <c>reqnroll/resolveTestTargets</c> request to the LSP server and maps the
/// result to <see cref="ScenarioTestTarget"/>s (design doc §3/§4, issue #262) — the VS-side
/// counterpart to <c>ResolveTestTargetsHandler</c>. Consumed by <c>RunTestCodeLensService</c> to
/// build the classic Run CodeLens bridge's data.
/// </summary>
internal sealed class ScenarioTestTargetService
{
    private const string RequestMethod = "reqnroll/resolveTestTargets";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<ScenarioTestTargetService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public ScenarioTestTargetService(LspInterceptingPipe pipe, ILogger<ScenarioTestTargetService> logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for the generated test method(s) that the scenario/Outline/example
    /// row at <paramref name="range"/> in <paramref name="fileUri"/> resolves to. A range covering
    /// a scenario's own header resolves to every target for that scenario; see the server's
    /// <c>IScenarioTestTargetResolver</c> for the full resolution rules.
    /// </summary>
    public async Task<IReadOnlyList<ScenarioTestTarget>> ResolveTestTargetsAsync(
        string fileUri, GherkinSymbolRange range, CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, range);

        _logger.LogInformation(
            "ScenarioTestTargetService: querying {RequestMethod} for {FileUri}:{StartLine}",
            RequestMethod, fileUri, range.Start.Line);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        var mapped = MapResult(result as JObject);
        _logger.LogInformation(
            "ScenarioTestTargetService: {TargetCount} target(s) returned for {FileUri}:{StartLine}",
            mapped.Count, fileUri, range.Start.Line);
        return mapped;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, GherkinSymbolRange range) =>
        new LspParamsBuilder()
            .AddTextDocument(fileUri)
            .AddRaw("range",
                "{\"start\":{\"line\":" + range.Start.Line + ",\"character\":" + range.Start.Character + "}," +
                "\"end\":{\"line\":" + range.End.Line + ",\"character\":" + range.End.Character + "}}")
            .Build();

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/resolveTestTargets</c> JSON result to a list of
    /// <see cref="ScenarioTestTarget"/>. Separated from transport so it can be unit-tested. A
    /// <c>null</c>, non-object, or missing-<c>targets</c> result yields an empty list. Entries
    /// missing <c>declaringTypeFullName</c>/<c>methodName</c> are skipped rather than throwing —
    /// same defensive shape as <c>GoToHooksService.ParseHooks</c>.
    /// </summary>
    internal static IReadOnlyList<ScenarioTestTarget> MapResult(JObject? result)
    {
        if (result?["targets"] is not JArray targetsArray)
            return Array.Empty<ScenarioTestTarget>();

        var list = new List<ScenarioTestTarget>(targetsArray.Count);
        foreach (var item in targetsArray)
        {
            if (item is not JObject obj) continue;

            var declaringTypeFullName = obj["declaringTypeFullName"]?.Value<string>();
            var methodName = obj["methodName"]?.Value<string>();
            if (string.IsNullOrEmpty(declaringTypeFullName) || string.IsNullOrEmpty(methodName))
                continue;

            var isParameterized = obj["isParameterized"]?.Value<bool>() ?? false;
            var rowIndex = obj["rowIndex"]?.Value<int?>();

            list.Add(new ScenarioTestTarget(declaringTypeFullName!, methodName!, isParameterized, rowIndex));
        }
        return list;
    }
}
