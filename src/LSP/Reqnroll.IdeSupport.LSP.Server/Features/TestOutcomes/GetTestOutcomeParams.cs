#nullable enable

using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>Request params for the custom <c>reqnroll/testOutcomes/getOutcome</c> request.</summary>
public sealed record GetTestOutcomeParams
{
    /// <summary>The generated test method's container assembly path (<c>TestOutcomeKey.Source</c>).</summary>
    [JsonProperty("assemblyPath")]
    public string AssemblyPath { get; set; } = "";

    [JsonProperty("typeFullName")]
    public string TypeFullName { get; set; } = "";

    [JsonProperty("methodName")]
    public string MethodName { get; set; } = "";
}
