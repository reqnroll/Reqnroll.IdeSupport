#nullable enable

using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>Response DTO for the custom <c>reqnroll/testOutcomes/registerRun</c> request.</summary>
public sealed class RegisterTestRunResponse
{
    /// <summary><see langword="true"/> when the server could mint a registration; <see langword="false"/> means "inject nothing for this run" (listener couldn't start).</summary>
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("runId")]
    public string? RunId { get; set; }

    [JsonProperty("endpoint")]
    public string? Endpoint { get; set; }

    [JsonProperty("token")]
    public string? Token { get; set; }
}
