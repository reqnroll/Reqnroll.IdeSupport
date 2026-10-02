#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;

/// <summary>Response DTO for the custom <c>reqnroll/findUnusedStepDefinitions</c> request (Find Unused Step Definitions).</summary>
public sealed class FindUnusedStepDefinitionsResponse
{
    /// <summary>Gets or sets the items.</summary>
    [JsonProperty("items")]
    public List<StepDefinitionItem> Items { get; set; } = new();
}
