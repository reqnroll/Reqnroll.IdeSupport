namespace Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

/// <summary>
/// Payload for the <c>reqnroll/testOutcomes/changed</c> server-to-client notification: a pure signal
/// ("re-pull outcomes for whatever you have open") with no fields, mirroring how
/// <c>reqnroll/refreshCodeLens</c> is used for the VS client today. Any IDE client may listen for this
/// and re-issue <c>reqnroll/testOutcomes/getOutcome</c> for its visible Run CodeLens lines.
/// </summary>
public sealed class TestOutcomesChangedParams;
