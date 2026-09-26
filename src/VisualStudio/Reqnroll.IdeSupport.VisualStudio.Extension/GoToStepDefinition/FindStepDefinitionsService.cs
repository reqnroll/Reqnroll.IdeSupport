#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Sends <c>reqnroll/findStepDefinitions</c> for a <c>.feature</c> caret position over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to step-definition rows (issue #757).
/// </summary>
/// <remarks>
/// The custom request returns the same bindings as <c>textDocument/definition</c> but with class,
/// method and binding expression, so the results list can show why a step is ambiguous — straight
/// from the server's live view of the bindings, not a line read back from a possibly unsaved file.
/// </remarks>
internal sealed class FindStepDefinitionsService
{
    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<FindStepDefinitionsService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public FindStepDefinitionsService(LspInterceptingPipe pipe, ILogger<FindStepDefinitionsService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the step definitions matching the step at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<IReadOnlyList<StepDefinitionListItem>> GetDefinitionsAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = LspParamsBuilder.TextDocumentPosition(fileUri, line0, char0);

        _logger.LogDebug(
            "FindStepDefinitionsService: querying {RequestMethod} at {FileUri}:{Line0}:{Char0}",
            ReqnrollMethodNames.FindStepDefinitions, fileUri, line0, char0);

        var result = await _pipe
            .SendRequestToServerAsync(ReqnrollMethodNames.FindStepDefinitions, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogTrace(
            "FindStepDefinitionsService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        var items = MapResult(result);
        _logger.LogDebug("FindStepDefinitionsService: {ItemCount} step definition(s) returned", items.Count);
        return items;
    }

    /// <summary>
    /// Pure mapping from a raw <c>reqnroll/findStepDefinitions</c> JSON result to step-definition
    /// rows, using the same item parser as Find Unused Step Definitions (the wire shape is shared).
    /// A <c>null</c> or non-object result yields no rows. Rows for the same method are collapsed:
    /// one method carrying two attributes that both match the step is returned once per binding,
    /// but is a single place to navigate to.
    /// </summary>
    internal static IReadOnlyList<StepDefinitionListItem> MapResult(JToken? result)
    {
        if (result is not JObject obj)
            return Array.Empty<StepDefinitionListItem>();

        var seen  = new HashSet<(string?, int, int)>();
        var items = new List<StepDefinitionListItem>();
        foreach (var item in FindUnusedStepDefinitionsService.ParseItems(obj["items"] as JArray ?? new JArray()))
        {
            var key = (item.SourceFile ?? item.RecordedSourceFile, item.SourceLine, item.SourceChar);
            if (seen.Add(key))
                items.Add(item);
        }
        return items;
    }
}
