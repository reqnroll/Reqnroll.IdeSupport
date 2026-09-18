#nullable enable

namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Request params for the custom <c>reqnroll/testOutcomes/registerRun</c> request: the IDE's
/// runsettings-injection service calls this to obtain the listener's endpoint and a fresh run id for
/// the bundled VSTest logger to report through (see <see cref="TestOutcomeTcpListener"/>'s remarks —
/// there is no per-connection secret to hand out). Carries no fields today; kept as an object rather
/// than <c>void</c> so a future IDE-identifying field can be added without a breaking wire-shape
/// change.
/// </summary>
public sealed record RegisterTestRunParams;
