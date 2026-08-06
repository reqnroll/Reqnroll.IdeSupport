#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.HookFeatureCodeLens;

/// <summary>
/// Sends a <c>textDocument/codeLens</c> request for a <c>.feature</c> file and maps the result to
/// <see cref="HookFeatureLensEntry"/> records (the classic-CodeLens hook-match-count bridge for
/// Visual Studio — issue #372, unblocking #269). Mirrors <c>StepCodeLensService</c>'s fetch shape,
/// but parses the richer <c>[uri, line, char, ownLevelOnly, alwaysShowPicker?]</c> argument list
/// <c>HookCodeLensHandler</c> emits, rather than <c>StepCodeLensService</c>'s <c>.cs</c>-attribute
/// shape.
/// </summary>
internal sealed class HookFeatureCodeLensService
{
    private const string RequestMethod = "textDocument/codeLens";

    private readonly LspInterceptingPipe _pipe;
    private readonly ILogger<HookFeatureCodeLensService> _logger;

    /// <summary>Creates the service over the given LSP transport pipe.</summary>
    public HookFeatureCodeLensService(LspInterceptingPipe pipe, ILogger<HookFeatureCodeLensService> logger)
    {
        _pipe   = pipe;
        _logger = logger;
    }

    /// <summary>
    /// Queries the LSP server for all hook-match-count lenses in <paramref name="fileUri"/>.
    /// Returns an empty list when the file has no hook bindings applicable, or has not yet been
    /// discovered.
    /// </summary>
    public async Task<IReadOnlyList<HookFeatureLensEntry>> GetLensesAsync(
        string            fileUri,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildParams(fileUri);

        _logger.LogInformation("HookFeatureCodeLensService: requesting {RequestMethod} for {FileUri}", RequestMethod, fileUri);

        var result = await _pipe
            .SendRequestToServerAsync(RequestMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Type == JTokenType.Null)
            return System.Array.Empty<HookFeatureLensEntry>();

        if (result is JArray array)
        {
            var items = ParseItems(array);
            _logger.LogInformation("HookFeatureCodeLensService: {LensCount} lens(es) returned for {FileUri}", items.Count, fileUri);
            return items;
        }

        _logger.LogInformation("HookFeatureCodeLensService: unexpected result token type {TokenType} for {FileUri}", result.Type, fileUri);
        return System.Array.Empty<HookFeatureLensEntry>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildParams(string fileUri) => LspParamsBuilder.TextDocumentUri(fileUri);

    /// <summary>
    /// Maps the server's <c>CodeLens[]</c> for a <c>.feature</c> file into lens entries. Internal
    /// rather than private so it can be unit-tested without a live pipe, matching
    /// <c>GoToHooksService.MapResult</c> and the other client-side mapping seams.
    /// </summary>
    internal static List<HookFeatureLensEntry> ParseItems(JArray array)
    {
        var result = new List<HookFeatureLensEntry>(array.Count);
        foreach (var token in array)
        {
            if (token is not JObject obj) continue;

            var rangeLine = obj["range"]?["start"]?["line"]?.Value<int>() ?? -1;
            if (rangeLine < 0) continue;

            var command = obj["command"] as JObject;
            var title   = command?["title"]?.Value<string>() ?? string.Empty;

            // Arguments from HookCodeLensHandler: [fileUri, navLine0, navChar0, ownLevelOnly, alwaysShowPicker?]
            var args             = command?["arguments"] as JArray;
            var argCount         = args?.Count ?? 0;
            var navLine          = argCount >= 2 ? args![1].Value<int>()  : rangeLine;
            var navChar          = argCount >= 3 ? args![2].Value<int>()  : 0;
            var ownLevelOnly     = argCount >= 4 && args![3].Value<bool>();
            var alwaysShowPicker = argCount >= 5 && args![4].Value<bool>();

            result.Add(new HookFeatureLensEntry(rangeLine, title, navLine, navChar, ownLevelOnly, alwaysShowPicker));
        }
        return result;
    }
}
