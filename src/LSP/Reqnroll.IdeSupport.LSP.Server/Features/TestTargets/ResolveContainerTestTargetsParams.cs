#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>Request params for the custom <c>reqnroll/resolveContainerTestTargets</c> request (issue #744).</summary>
public sealed record ResolveContainerTestTargetsParams
{
    /// <summary>The <c>.feature</c> document to resolve test targets in.</summary>
    [JsonProperty("textDocument")]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    /// <summary>
    /// The container's own range — a <c>Feature:</c> or <c>Rule:</c> block's full body (not just its
    /// header line). Every Scenario/Scenario Outline fully contained within this range is resolved,
    /// including those nested in a <c>Rule:</c> when the range is the enclosing Feature's.
    /// </summary>
    [JsonProperty("range")]
    public LspRange Range { get; set; } = null!;
}
