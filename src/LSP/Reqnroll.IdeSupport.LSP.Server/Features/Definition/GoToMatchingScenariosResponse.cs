#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Response DTO for the custom <c>reqnroll/goToMatchingScenarios</c> request (issue #373's
/// hook-match-count CodeLens click action). Contains every scenario the queried hook binding's
/// scope matches, across the whole owning project(s) -- the inverse of
/// <c>reqnroll/goToHooks</c>'s <see cref="GoToHooksResponse"/>.
/// </summary>
public sealed class GoToMatchingScenariosResponse
{
    /// <summary>Gets or sets the matching scenarios.</summary>
    [JsonProperty("scenarios")]
    public List<MatchingScenarioLocation> Scenarios { get; set; } = new();
}

/// <summary>One scenario matched by the queried hook's scope.</summary>
public sealed class MatchingScenarioLocation
{
    /// <summary>Feature file URI (e.g. <c>file:///C:/project/Calculator.feature</c>).</summary>
    [JsonProperty("uri")]
    public string Uri { get; set; } = "";

    /// <summary>0-based line of the scenario/scenario outline definition in the feature file.</summary>
    [JsonProperty("startLine")]
    public int StartLine { get; set; }

    /// <summary>0-based character of the scenario/scenario outline definition in the feature file.</summary>
    [JsonProperty("startChar")]
    public int StartChar { get; set; }

    /// <summary>The scenario's title, or an empty string if untitled.</summary>
    [JsonProperty("scenarioName")]
    public string ScenarioName { get; set; } = "";

    /// <summary>True when this is a Scenario Outline definition (counted once regardless of its Examples row count).</summary>
    [JsonProperty("isOutline")]
    public bool IsOutline { get; set; }
}
