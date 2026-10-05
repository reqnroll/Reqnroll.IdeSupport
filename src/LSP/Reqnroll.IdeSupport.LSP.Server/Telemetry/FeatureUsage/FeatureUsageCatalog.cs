using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry.FeatureUsage;

/// <summary>How a counted feature is triggered; reported in separate <c>FeatureUsageSummary</c> properties.</summary>
public enum FeatureUsageKind
{
    /// <summary>Requested by the editor on a user gesture or typing (completion, lightbulb evaluation).</summary>
    Lookup,

    /// <summary>Requested by the editor on its own schedule (inlay hints, folding, outline, CodeLens render, on-type formatting).</summary>
    Passive,
}

/// <summary>One counted feature: the stable counter key it is reported under, and its <see cref="FeatureUsageKind"/>.</summary>
public readonly record struct FeatureUsageEntry(string Key, FeatureUsageKind Kind);

/// <summary>
/// The closed, code-defined catalogue of volume signals counted by <see cref="IFeatureUsageCounters"/>
/// (issue #582): lookups and passive editor-driven requests that are too frequent to send as one
/// telemetry event each. Maps an <see cref="IOperationDurationRecorder"/> operation label to a
/// feature-level counter key.
/// </summary>
/// <remarks>
/// <para>
/// Discrete user commands are deliberately <b>not</b> here: Go to Step Definition, Find Usages,
/// Rename, Find Unused, Comment/Uncomment, Format, Run lens lookups, Find Hooks and Go to Matching
/// Scenarios all already send a per-call event carrying characteristic properties (#849), and
/// counting them as well would double-count every command. Their invocation counts come from those
/// events. A guard test pins that no catalogue operation is also a per-call command.
/// </para>
/// <para>
/// Membership is a strawman pending the scope decision in issue #583 / coverage gaps in #850.
/// Continuous plumbing (document sync, semantic tokens, diagnostics publication, workspace refresh
/// notifications, internal reconciliation) says nothing about feature use and is excluded.
/// </para>
/// <para>
/// Privacy: keys are fixed identifiers from this table; no user data can reach a counter.
/// </para>
/// </remarks>
public static class FeatureUsageCatalog
{
    private const string TagCompletionOperation = LspStandardMethodNames.TextDocumentCompletion + "#tag";
    private const string KeywordCompletionOperation = LspStandardMethodNames.TextDocumentCompletion + "#keyword";
    private const string StepCompletionOperation = LspStandardMethodNames.TextDocumentCompletion + "#step";

    private static readonly Dictionary<string, FeatureUsageEntry> Entries = new(StringComparer.Ordinal)
    {
        [StepCompletionOperation] = new("Completion.Step", FeatureUsageKind.Lookup),
        [KeywordCompletionOperation] = new("Completion.Keyword", FeatureUsageKind.Lookup),
        // Nested inside a keyword completion whenever the tag branch runs, so Completion.Tag is a
        // subset of Completion.Keyword, not an addition to it.
        [TagCompletionOperation] = new("Completion.Tag", FeatureUsageKind.Lookup),
        [LspStandardMethodNames.TextDocumentCompletion] = new("Completion.Other", FeatureUsageKind.Lookup),
        // Fires on cursor moves in VS and VS Code (lightbulb evaluation), not only on click.
        [LspStandardMethodNames.TextDocumentCodeAction] = new("CodeAction", FeatureUsageKind.Lookup),

        [LspStandardMethodNames.TextDocumentCodeLens] = new("CodeLens", FeatureUsageKind.Passive),
        [LspStandardMethodNames.TextDocumentInlayHint] = new("InlayHint", FeatureUsageKind.Passive),
        [LspStandardMethodNames.TextDocumentFoldingRange] = new("FoldingRange", FeatureUsageKind.Passive),
        [LspStandardMethodNames.TextDocumentDocumentLink] = new("DocumentLink", FeatureUsageKind.Passive),
        [LspStandardMethodNames.TextDocumentDocumentSymbol] = new("DocumentSymbol", FeatureUsageKind.Passive),
        [CustomLspMethodNames.ReqnrollDocumentSymbolHierarchical] = new("DocumentSymbol", FeatureUsageKind.Passive),
        // Closes the gap the retired CommandAutoFormatTable event left: on-type table formatting
        // fires per keystroke, so only an aggregate count is meaningful.
        [LspStandardMethodNames.TextDocumentOnTypeFormatting] = new("OnTypeFormatting", FeatureUsageKind.Passive),
    };

    /// <summary>
    /// Counter key for an accepted step completion (issue #883 prototype). Not tied to an operation
    /// label: it is incremented by the completion-accepted command handler, not the duration recorder.
    /// </summary>
    public const string StepCompletionAcceptedKey = "Completion.Step.Accepted";

    private static readonly Dictionary<string, FeatureUsageKind> KindsByKey = Entries.Values
        .DistinctBy(e => e.Key)
        .Append(new FeatureUsageEntry(StepCompletionAcceptedKey, FeatureUsageKind.Lookup))
        .ToDictionary(e => e.Key, e => e.Kind, StringComparer.Ordinal);

    /// <summary>Looks up the counter entry for an <see cref="IOperationDurationRecorder"/> operation label.</summary>
    public static bool TryGet(string operation, out FeatureUsageEntry entry) => Entries.TryGetValue(operation, out entry);

    /// <summary>Whether <paramref name="operation"/> is counted.</summary>
    public static bool IsCounted(string operation) => Entries.ContainsKey(operation);

    /// <summary>The <see cref="FeatureUsageKind"/> of a counter key, or <c>null</c> if the key is not in the catalogue.</summary>
    public static FeatureUsageKind? KindOf(string key) => KindsByKey.TryGetValue(key, out var kind) ? kind : null;

    /// <summary>Every operation label in the catalogue.</summary>
    public static IEnumerable<string> Operations => Entries.Keys;
}
