#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Shared core for all three "Find Step Usages" surfaces (Find Step Definition Usages / Find All References, design doc section P3).
/// Sends a custom <c>reqnroll/findStepUsages</c> request over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to a <see cref="StepUsagesResult"/>.
/// </summary>
/// <remarks>
/// Uses the custom <c>reqnroll/findStepUsages</c> request (design doc section P2b) rather than
/// <c>textDocument/references</c> to obtain the full three-state contract:
/// <list type="bullet">
///   <item>Server returns JSON <c>null</c> → <see cref="StepUsagesResult.NotABinding"/> (caller shows an
///         informational message; there is no Surface-3 takeover of the built-in command — see remarks below).</item>
///   <item>Server returns <c>{"isBinding":true,"locations":[]}</c> → binding present, 0 usages.</item>
///   <item>Server returns <c>{"isBinding":true,"locations":[...]}</c> → matching feature-file steps.</item>
/// </list>
/// Each location includes a <c>stepText</c> field supplied directly by the server from the
/// in-memory document snapshot, so no disk I/O is required on the client side.
/// </remarks>
internal sealed class FindStepUsagesService
{
    // Method name for the custom request — distinct from textDocument/references so the server
    // can deliver null and per-location stepText that the standard LSP method cannot carry.
    private const string RequestMethod = "reqnroll/findStepUsages";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<FindStepUsagesService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public FindStepUsagesService(LspInterceptingPipe pipe, ILogger<FindStepUsagesService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for step usages at <paramref name="line0"/> / <paramref name="char0"/>
    /// in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<StepUsagesResult> FindUsagesAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri, line0, char0);

        _logger.LogInformation(
            "FindStepUsagesService: querying {RequestMethod} at {FileUri}:{Line0}:{Char0}", RequestMethod, fileUri, line0, char0);

        _logger.LogInformation(
            "FindStepUsagesService: sending {RequestMethod} params={ParamsJson}", RequestMethod, paramsJson);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        // NOTE: use the parameterless JToken.ToString() — the overload that takes
        // Newtonsoft.Json.Formatting throws MissingMethodException against the Newtonsoft version
        // that VS loads at runtime.
        _logger.LogInformation(
            "FindStepUsagesService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        // Map transport result → three-state StepUsagesResult. The mapping is a pure function
        // (MapResult) so it can be unit-tested without a live pipe.
        var mapped = MapResult(result);
        _logger.LogInformation(
            "FindStepUsagesService: {ResultSummary}",
            mapped.IsBinding ? $"{mapped.Locations.Count} location(s) returned" : "NotABinding");
        return mapped;
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/findStepUsages</c> JSON result to the three-state
    /// <see cref="StepUsagesResult"/>. Separated from transport so it can be unit-tested.
    /// <list type="bullet">
    ///   <item>JSON <c>null</c> / non-object → <see cref="StepUsagesResult.NotABinding"/>.</item>
    ///   <item><c>{"isBinding":false}</c> (or missing) → <see cref="StepUsagesResult.NotABinding"/>.</item>
    ///   <item><c>{"isBinding":true,"locations":[...]}</c> → binding with parsed locations.</item>
    /// </list>
    /// (The server avoids serialising JSON <c>null</c> for custom response types — OmniSharp's
    /// OnRequest framework sends an error response instead — but the null guard is kept in case
    /// of a server-version mismatch.)
    /// </summary>
    internal static StepUsagesResult MapResult(JToken? result)
    {
        if (result is null || result.Type == JTokenType.Null)
            return StepUsagesResult.NotABinding;

        if (result is JObject obj)
        {
            var isBinding = obj["isBinding"]?.Value<bool>() ?? false;
            if (!isBinding)
                return StepUsagesResult.NotABinding;

            var locationsArray = obj["locations"] as JArray ?? new JArray();
            return new StepUsagesResult(ParseLocations(locationsArray));
        }

        return StepUsagesResult.NotABinding;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri, int line0, int char0) =>
        // Same request params shape as textDocument/references (textDocument URI + position).
        // includeDeclaration omitted — reqnroll/findStepUsages ignores it but keep the field
        // for structural parity so any future tracing is recognisable as a references variant.
        new LspParamsBuilder()
            .AddTextDocument(fileUri)
            .AddPosition(line0, char0)
            .AddRaw("context", "{\"includeDeclaration\":false}")
            .Build();

    private static IReadOnlyList<StepUsageLocation> ParseLocations(JArray array)
    {
        var result = new List<StepUsageLocation>(array.Count);
        foreach (var item in array)
        {
            if (item is not JObject obj) continue;

            var uri = obj["uri"]?.Value<string>();
            if (uri is null) continue;

            var stepText    = obj["stepText"]?.Value<string>();
            var keyword     = obj["keyword"]?.Value<string>();
            var scenarioName = obj["scenarioName"]?.Value<string>();
            var projectName  = obj["projectName"]?.Value<string>();

            var startLine = obj["startLine"]?.Value<int>() ?? 0;
            var startChar = obj["startChar"]?.Value<int>() ?? 0;
            var endLine   = obj["endLine"]?.Value<int>()   ?? 0;
            var endChar   = obj["endChar"]?.Value<int>()   ?? 0;

            result.Add(new StepUsageLocation(
                uri, startLine, startChar, endLine, endChar,
                stepText, keyword, scenarioName, projectName));
        }
        return result;
    }
}
