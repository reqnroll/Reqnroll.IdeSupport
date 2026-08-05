#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;

/// <summary>Response DTO for the custom <c>reqnroll/resolveTestTargets</c> request (design doc §4).</summary>
public sealed class ResolveTestTargetsResponse
{
    /// <summary>Gets or sets the resolved test target(s).</summary>
    [JsonProperty("targets")]
    public List<ScenarioTestTargetDto> Targets { get; set; } = new();
}

/// <summary>One generated test method (or one row of a row-tests-parameterized method) a scenario/row resolves to. See design doc §3.</summary>
public sealed class ScenarioTestTargetDto
{
    /// <summary>The generated test class's full name, e.g. <c>Discovery_PlatformCompatibilityFeature</c>.</summary>
    [JsonProperty("declaringTypeFullName")]
    public string DeclaringTypeFullName { get; set; } = "";

    /// <summary>The generated method name.</summary>
    [JsonProperty("methodName")]
    public string MethodName { get; set; } = "";

    /// <summary>
    /// <see langword="true"/> when <see cref="MethodName"/> is a row-tests Scenario Outline method
    /// (multiple targets share the same method, distinguished by <see cref="RowIndex"/>).
    /// </summary>
    [JsonProperty("isParameterized")]
    public bool IsParameterized { get; set; }

    /// <summary>The row's argument values by column header, when known. <see langword="null"/> otherwise.</summary>
    [JsonProperty("rowArguments")]
    public Dictionary<string, string>? RowArguments { get; set; }

    /// <summary>The 0-based position of this target among the method's row-attribute instances. <see langword="null"/> unless <see cref="IsParameterized"/>.</summary>
    [JsonProperty("rowIndex")]
    public int? RowIndex { get; set; }
}
