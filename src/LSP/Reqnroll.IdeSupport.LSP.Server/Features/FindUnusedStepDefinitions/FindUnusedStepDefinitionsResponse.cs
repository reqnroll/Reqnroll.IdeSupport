#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;

/// <summary>Response DTO for the custom <c>reqnroll/findUnusedStepDefinitions</c> request (Find Unused Step Definitions).</summary>
public sealed class FindUnusedStepDefinitionsResponse
{
    /// <summary>Gets or sets the items.</summary>
    [JsonProperty("items")]
    public List<UnusedStepDefinitionItem> Items { get; set; } = new();
}

/// <summary>One step-definition binding that has zero matching steps across the workspace.</summary>
public sealed class UnusedStepDefinitionItem
{
    /// <summary>Short project name that owns this step-definition binding.</summary>
    [JsonProperty("projectName")]
    public string? ProjectName { get; set; }

    /// <summary>Declaring class name (last segment of the qualified type name).</summary>
    [JsonProperty("className")]
    public string? ClassName { get; set; }

    /// <summary>Method name (without parameters or return type).</summary>
    [JsonProperty("methodName")]
    public string? MethodName { get; set; }

    /// <summary>The binding expression as written in the step attribute, e.g. <c>"the sum is {int}"</c>.</summary>
    [JsonProperty("bindingExpression")]
    public string? BindingExpression { get; set; }

    /// <summary>Absolute path of the C# source file containing this binding.</summary>
    [JsonProperty("sourceFile")]
    public string? SourceFile { get; set; }

    /// <summary>0-based line of the binding method in <see cref="SourceFile"/>.</summary>
    [JsonProperty("sourceLine")]
    public int SourceLine { get; set; }

    /// <summary>0-based column of the binding method in <see cref="SourceFile"/>.</summary>
    [JsonProperty("sourceChar")]
    public int SourceChar { get; set; }
}
