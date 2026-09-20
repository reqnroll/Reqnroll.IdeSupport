#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>Response DTO for the custom <c>reqnroll/testOutcomes/getOutcome</c> request.</summary>
public sealed class GetTestOutcomeResponse
{
    /// <summary><see langword="false"/> means "no run reported this method this session" — the client should fall back to its own reflection bridge, exactly like a null result used to.</summary>
    [JsonProperty("found")]
    public bool Found { get; set; }

    /// <summary>Aggregate over <see cref="Rows"/>: <c>Failed</c> &gt; <c>Passed</c> &gt; <c>Skipped</c> &gt; other.</summary>
    [JsonProperty("aggregate")]
    public string Aggregate { get; set; } = "";

    [JsonProperty("rows")]
    public List<TestOutcomeRowDto> Rows { get; set; } = new();

    [JsonProperty("lastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; set; }

    /// <summary>A run naming this method has started and not yet completed.</summary>
    [JsonProperty("isRunning")]
    public bool IsRunning { get; set; }

    /// <summary>The container assembly was rebuilt after these outcomes were recorded; they describe old code.</summary>
    [JsonProperty("isStale")]
    public bool IsStale { get; set; }
}

/// <summary>One row (test case) of a method's last-known outcome.</summary>
public sealed class TestOutcomeRowDto
{
    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary><c>Passed</c>/<c>Failed</c>/<c>Skipped</c>/<c>NotFound</c>/<c>None</c> — the <c>TestOutcomeKind</c> name.</summary>
    [JsonProperty("outcome")]
    public string Outcome { get; set; } = "";

    [JsonProperty("durationMs")]
    public double DurationMs { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>Number of steps Reqnroll traced for this row (0 when the output carried no trace).</summary>
    [JsonProperty("stepCount")]
    public int StepCount { get; set; }

    /// <summary>0-based execution index of the first failing step, or null.</summary>
    [JsonProperty("failedStepIndex")]
    public int? FailedStepIndex { get; set; }

    /// <summary>The traced text of that step (keyword + text), or null.</summary>
    [JsonProperty("failedStepText")]
    public string? FailedStepText { get; set; }

    /// <summary><c>Error</c>/<c>BindingError</c>/<c>Undefined</c> — the <c>StepTraceOutcome</c> name, or null.</summary>
    [JsonProperty("failedStepOutcome")]
    public string? FailedStepOutcome { get; set; }
}
