#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Response DTO for the custom <c>reqnroll/findHooks</c> request (Hook Navigation — "Go to Hooks").
/// Contains every hook binding applicable at the queried <c>.feature</c> file position,
/// filtered by hook-type level (Feature / Scenario / Step) and tag/scope expressions.
/// </summary>
public sealed class FindHooksResponse
{
    /// <summary>Gets or sets the hooks.</summary>
    [JsonProperty("hooks")]
    public List<FindHookLocation> Hooks { get; set; } = new();
}

/// <summary>One applicable hook binding at the queried position.</summary>
public sealed class FindHookLocation
{
    /// <summary>C# source file URI (e.g. <c>file:///C:/project/Hooks.cs</c>).</summary>
    [JsonProperty("uri")]
    public string Uri { get; set; } = "";

    /// <summary>0-based line of the hook method declaration in the source file.</summary>
    [JsonProperty("startLine")]
    public int StartLine { get; set; }

    /// <summary>0-based character of the hook method declaration in the source file.</summary>
    [JsonProperty("startChar")]
    public int StartChar { get; set; }

    /// <summary>Hook attribute name (e.g. <c>"BeforeScenario"</c>, <c>"AfterStep"</c>).</summary>
    [JsonProperty("hookType")]
    public string HookType { get; set; } = "";

    /// <summary>Execution order of the hook (default 10000).</summary>
    [JsonProperty("hookOrder")]
    public int HookOrder { get; set; }

    /// <summary>Short C# method name of the hook (e.g. <c>"SetUpDatabase"</c>).</summary>
    [JsonProperty("methodName")]
    public string MethodName { get; set; } = "";
}
