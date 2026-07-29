#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

/// <summary>
/// Sends a <c>textDocument/codeLens</c> request to the LSP server and maps the result to a
/// list of <see cref="StepLensItem"/> records.
/// </summary>
/// <remarks>
/// VS.Extensibility <c>ICodeLensProvider</c> calls this once per file refresh; the result is
/// shared across all code-element lenses in the same file so the LSP round-trip is amortised.
/// </remarks>
internal sealed class StepCodeLensService
{
    private const string RequestMethod = "textDocument/codeLens";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<StepCodeLensService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public StepCodeLensService(LspInterceptingPipe pipe, ILogger<StepCodeLensService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for all step-binding lenses in <paramref name="fileUri"/>.
    /// Returns an empty list when the file has no step-binding attributes or has not yet
    /// been discovered.
    /// </summary>
    public async Task<IReadOnlyList<StepLensItem>> GetLensesAsync(
        string            fileUri,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri);

        _logger.LogInformation("StepCodeLensService: requesting {RequestMethod} for {FileUri}", RequestMethod, fileUri);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "StepCodeLensService: raw result = {Result}", result is null ? "<null>" : result.ToString());

        if (result is null || result.Type == JTokenType.Null)
        {
            _logger.LogInformation("StepCodeLensService: server returned null — no lenses");
            return System.Array.Empty<StepLensItem>();
        }

        if (result is JArray array)
        {
            var items = ParseItems(array);
            _logger.LogInformation(
                "StepCodeLensService: {LensCount} lens(es) returned for {FileUri}", items.Count, fileUri);
            return items;
        }

        _logger.LogInformation(
            "StepCodeLensService: unexpected result token type {TokenType} for {FileUri}", result.Type, fileUri);
        return System.Array.Empty<StepLensItem>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri)
    {
        var escapedUri = Newtonsoft.Json.JsonConvert.ToString(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}}}}";
    }

    private static IReadOnlyList<StepLensItem> ParseItems(JArray array)
    {
        var result = new List<StepLensItem>(array.Count);
        foreach (var token in array)
        {
            if (token is not JObject obj) continue;

            var rangeLine = obj["range"]?["start"]?["line"]?.Value<int>() ?? -1;
            if (rangeLine < 0) continue;

            var command     = obj["command"] as JObject;
            var title       = command?["title"]?.Value<string>() ?? string.Empty;
            var commandName = command?["command"]?.Value<string>() ?? string.Empty;

            // Arguments from the server: [fileUri, attrLine0, attrChar0] — the attribute's exact
            // position, needed verbatim by position-sensitive lookups like goToMatchingScenarios.
            var args         = command?["arguments"] as JArray;
            var argLine      = args?.Count >= 2 ? args[1].Value<int>() : rangeLine;
            var argChar      = args?.Count >= 3 ? args[2].Value<int>() : 0;

            result.Add(new StepLensItem(rangeLine, title, commandName, argLine, argChar));
        }
        return result;
    }
}

/// <summary>One code-lens item returned by the LSP server for a step-binding attribute.</summary>
internal sealed record StepLensItem(
    /// <summary>0-based line of the lens's range (the method-declaration line).</summary>
    int    RangeLine,
    /// <summary>Display title, e.g. <c>"N step usages"</c>.</summary>
    string Title,
    /// <summary>Name of the command the lens invokes when clicked.</summary>
    string CommandName,
    /// <summary>0-based line of the step-binding attribute the command should target.</summary>
    int    ArgLine,
    /// <summary>0-based column of the step-binding attribute the command should target.</summary>
    int    ArgChar);
