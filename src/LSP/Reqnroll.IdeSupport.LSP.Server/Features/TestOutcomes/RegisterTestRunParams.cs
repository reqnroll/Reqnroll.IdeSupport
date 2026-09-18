#nullable enable

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Request params for the custom <c>reqnroll/testOutcomes/registerRun</c> request: the IDE's
/// runsettings-injection service calls this once per Test Explorer execution request to obtain a
/// fresh, single-use endpoint+token for the bundled VSTest logger to report through (see
/// <see cref="TestOutcomeTcpListener"/>'s remarks on why a per-run token, not one long-lived value).
/// Carries no fields today; kept as an object rather than <c>void</c> so a future IDE-identifying
/// field can be added without a breaking wire-shape change.
/// </summary>
public sealed record RegisterTestRunParams;
