#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>Request params for the custom <c>reqnroll/resolveTestTargets</c> request (design doc §4).</summary>
public sealed record ResolveTestTargetsParams
{
    /// <summary>The <c>.feature</c> document to resolve test targets in.</summary>
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    /// <summary>
    /// The range to resolve at. A range within a scenario/Outline's own header or steps resolves to
    /// every target for that scenario (e.g. every Outline row); a range within one specific
    /// <c>Examples:</c> row resolves to just that row's target.
    /// </summary>
    [JsonProperty("range")]
    public LspRange Range { get; set; } = null!;
}
