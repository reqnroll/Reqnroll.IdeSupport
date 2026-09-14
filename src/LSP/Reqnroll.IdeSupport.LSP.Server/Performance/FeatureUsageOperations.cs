using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Performance;

/// <summary>
/// The closed set of <see cref="IOperationDurationRecorder"/> operation labels counted by
/// <see cref="IFeatureUsageCounters"/> (issue #582) — discrete user commands invoked a handful of
/// times per session, as opposed to continuous/passive operations that fire on every keystroke or
/// viewport change (completion, semantic tokens, folding ranges, document sync, …), which would
/// dominate any usage chart and drown out the signal this exists to surface.
/// </summary>
/// <remarks>
/// This is a strawman allowlist pending the scope decision in issue #583 — membership here is
/// expected to change once that resolves. Deliberately not "everything in <see cref="LspMethodNames"/>":
/// the label space also includes non-<see cref="LspMethodNames"/> strings such as
/// <c>CommentToggleHandler</c>'s <c>"reqnroll.toggleComment"</c> command name and
/// <c>CompletionHandler</c>'s derived <c>textDocument/completion#keyword</c>/<c>#step</c> labels —
/// the latter deliberately excluded here as continuous/passive, per
/// <c>CommandAutoFormatTable</c>'s existing precedent in the architecture doc's telemetry section.
/// </remarks>
public static class FeatureUsageOperations
{
    private static readonly HashSet<string> Counted = new(StringComparer.Ordinal)
    {
        LspMethodNames.TextDocumentDefinition,
        LspMethodNames.TextDocumentReferences,
        LspMethodNames.ReqnrollFindStepUsages,
        LspMethodNames.ReqnrollGoToHooks,
        LspMethodNames.ReqnrollGoToMatchingScenarios,
        LspMethodNames.ReqnrollFindUnusedStepDefinitions,
        LspMethodNames.ReqnrollRenameTargets,
        LspMethodNames.TextDocumentRename,
        LspMethodNames.TextDocumentPrepareRename,
        LspMethodNames.ReqnrollSelectRenameTarget,
        LspMethodNames.TextDocumentCodeAction,
        LspMethodNames.TextDocumentFormatting,
        LspMethodNames.TextDocumentRangeFormatting,
        LspMethodNames.ReqnrollResolveTestTargets,
        "reqnroll.toggleComment",
    };

    /// <summary>Whether <paramref name="operation"/> is a discrete command counted for feature-usage telemetry.</summary>
    public static bool IsCounted(string operation) => Counted.Contains(operation);
}
