#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>
/// Response DTO for the custom <c>reqnroll/resolveContainerTestTargets</c> request (issue #744).
/// Shares <see cref="ScenarioTestTargetDto"/> with <see cref="ResolveTestTargetsResponse"/> — one
/// target looks the same whether it came from a single-scenario or a whole-container resolution.
/// </summary>
public sealed class ResolveContainerTestTargetsResponse
{
    /// <summary>Gets or sets every resolved test target across the container's scenarios.</summary>
    [JsonProperty("targets")]
    public List<ScenarioTestTargetDto> Targets { get; set; } = new();
}
