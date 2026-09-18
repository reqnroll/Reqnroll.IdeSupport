namespace Reqnroll.IdeSupport.VisualStudio.TestLogger;

/// <summary>
/// What the LSP server's <c>reqnroll/testOutcomes/registerRun</c> request hands back for one run
/// (LSP-server outcome pipeline refactor) — mapped from the raw JSON response by
/// <c>Reqnroll.IdeSupport.VisualStudio.Extension.TestOutcomes.RunTestOutcomeService</c>.
/// </summary>
public sealed record TestRunRegistration(string RunId, string Endpoint);
