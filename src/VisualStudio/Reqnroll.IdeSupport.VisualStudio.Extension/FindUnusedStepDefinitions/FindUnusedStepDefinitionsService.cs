#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Sends a custom <c>reqnroll/findUnusedStepDefinitions</c> request over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to an
/// <see cref="UnusedStepDefinitionsResult"/>.
/// </summary>
internal sealed class FindUnusedStepDefinitionsService
{
    private const string RequestMethod = "reqnroll/findUnusedStepDefinitions";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<FindUnusedStepDefinitionsService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public FindUnusedStepDefinitionsService(LspInterceptingPipe pipe, ILogger<FindUnusedStepDefinitionsService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>Queries the LSP server for the workspace-wide set of unused step definitions.</summary>
    public async Task<UnusedStepDefinitionsResult> FindUnusedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FindUnusedStepDefinitionsService: sending {RequestMethod}", RequestMethod);

        // Empty params object — the server ignores the body.
        const string emptyParams = "{}";

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, emptyParams, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "FindUnusedStepDefinitionsService: raw result = {Result}", result is null ? "<null>" : result.ToString());

        var mapped = MapResult(result);
        _logger.LogInformation(
            "FindUnusedStepDefinitionsService: {ItemCount} unused step definition(s)", mapped.Items.Count);
        return mapped;
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/findUnusedStepDefinitions</c> JSON result to an
    /// <see cref="UnusedStepDefinitionsResult"/>. Separated from transport so it can be unit-tested.
    /// A <c>null</c>, non-object, or missing-<c>items</c> result yields
    /// <see cref="UnusedStepDefinitionsResult.Empty"/>.
    /// </summary>
    internal static UnusedStepDefinitionsResult MapResult(JToken? result)
    {
        if (result is null || result.Type == JTokenType.Null)
            return UnusedStepDefinitionsResult.Empty;

        if (result is JObject obj)
            return new UnusedStepDefinitionsResult(ParseItems(obj["items"] as JArray ?? new JArray()));

        return UnusedStepDefinitionsResult.Empty;
    }

    private static IReadOnlyList<UnusedStepLocation> ParseItems(JArray array)
    {
        var result = new List<UnusedStepLocation>(array.Count);
        foreach (var token in array)
        {
            if (token is not JObject item) continue;
            result.Add(new UnusedStepLocation
            {
                ProjectName       = item["projectName"]?.Value<string>(),
                ClassName         = item["className"]?.Value<string>(),
                MethodName        = item["methodName"]?.Value<string>(),
                BindingExpression = item["bindingExpression"]?.Value<string>(),
                SourceFile        = item["sourceFile"]?.Value<string>(),
                SourceLine        = item["sourceLine"]?.Value<int>() ?? 0,
                SourceChar        = item["sourceChar"]?.Value<int>() ?? 0,
                // Absent on an older server, which never reported an unresolvable source; default
                // to true there so the row stays navigable exactly as it was (issue #540).
                IsResolved        = item["isResolved"]?.Value<bool>() ?? true,
                RecordedSourceFile = item["recordedSourceFile"]?.Value<string>(),
            });
        }
        return result;
    }
}
