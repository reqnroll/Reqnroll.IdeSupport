#nullable enable

using Newtonsoft.Json;
using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Request params for the custom <c>reqnroll/testOutcomes/registerRun</c> request: the IDE's
/// runsettings-injection service calls this to obtain the listener's endpoint and a fresh run id for
/// the bundled VSTest logger to report through (see <see cref="TestOutcomeTcpListener"/>'s remarks —
/// there is no per-connection secret to hand out). Kept as an object rather than <c>void</c> so
/// optional fields can be added without a breaking wire-shape change.
/// </summary>
public sealed record RegisterTestRunParams
{
    /// <summary>
    /// How the user started the run: one of the <see cref="TestRunModes"/> values. Absent means "not a user-started
    /// run" (VS Code registers once at activation, not per run) and nothing is counted; an unrecognised
    /// value is likewise ignored, so a client string can never become a telemetry key (issue #850).
    /// </summary>
    [JsonProperty("runMode")]
    public string? RunMode { get; set; }
}
