#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>Response DTO for the custom <c>reqnroll/findStepDefinitions</c> request (issue #757).</summary>
public sealed class FindStepDefinitionsResponse
{
    /// <summary>The bindings matching the step at the requested position, in match order; empty when there is no step or no binding.</summary>
    [JsonProperty("items")]
    public List<StepDefinitionItem> Items { get; set; } = new();
}
