#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Sends <c>textDocument/definition</c> for a <c>.feature</c> caret position over the owned
/// <see cref="LspInterceptingPipe"/> and maps the result to step-definition locations (issue #757).
/// </summary>
/// <remarks>
/// This is the same request VS's own LSP client sends for Go To Definition; the extension sends it
/// itself only so it can present multiple results under a title naming the step (see
/// <c>GoToDefinitionCommandFilter</c>).
/// </remarks>
internal sealed class GoToStepDefinitionService
{
    private const string RequestMethod = "textDocument/definition";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<GoToStepDefinitionService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public GoToStepDefinitionService(LspInterceptingPipe pipe, ILogger<GoToStepDefinitionService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the step definitions matching the step at <paramref name="line0"/> /
    /// <paramref name="char0"/> in <paramref name="fileUri"/> (all 0-based).
    /// </summary>
    public async Task<IReadOnlyList<StepDefinitionLocation>> GetDefinitionsAsync(
        string            fileUri,
        int               line0,
        int               char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = LspParamsBuilder.TextDocumentPosition(fileUri, line0, char0);

        _logger.LogDebug(
            "GoToStepDefinitionService: querying {RequestMethod} at {FileUri}:{Line0}:{Char0}", RequestMethod, fileUri, line0, char0);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogTrace(
            "GoToStepDefinitionService: raw server result = {Result}", result is null ? "<null>" : result.ToString());

        var locations = MapResult(result);
        _logger.LogDebug("GoToStepDefinitionService: {LocationCount} location(s) returned", locations.Count);
        return locations;
    }

    /// <summary>
    /// Pure mapping from a raw <c>textDocument/definition</c> JSON result — <c>null</c>, a single
    /// <c>Location</c>, a <c>Location[]</c> or a <c>LocationLink[]</c> — to
    /// <see cref="StepDefinitionLocation"/>s. Separated from transport so it can be unit-tested.
    /// Entries without a URI or start position are skipped, and repeats of the same position are
    /// collapsed: one method carrying two attributes that both match the step is returned once per
    /// binding, but is a single place to navigate to.
    /// </summary>
    internal static IReadOnlyList<StepDefinitionLocation> MapResult(JToken? result)
    {
        IEnumerable<JToken> items = result switch
        {
            JArray array => array,
            JObject obj  => new[] { obj },
            _            => Array.Empty<JToken>(),
        };

        var locations = new List<StepDefinitionLocation>();
        foreach (var item in items)
        {
            if (item is not JObject obj) continue;

            // Location: { uri, range }; LocationLink: { targetUri, targetSelectionRange, targetRange }.
            var uri   = obj["uri"]?.Value<string>() ?? obj["targetUri"]?.Value<string>();
            var range = obj["range"] ?? obj["targetSelectionRange"] ?? obj["targetRange"];
            var line  = range?["start"]?["line"]?.Value<int>();
            var ch    = range?["start"]?["character"]?.Value<int>();

            if (uri is null || line is null || ch is null) continue;

            var location = new StepDefinitionLocation(uri, line.Value, ch.Value);
            if (!locations.Contains(location))
                locations.Add(location);
        }
        return locations;
    }
}

/// <summary>One step-definition location returned by <c>textDocument/definition</c>; positions are 0-based.</summary>
internal sealed record StepDefinitionLocation(string FileUri, int StartLine, int StartChar);
