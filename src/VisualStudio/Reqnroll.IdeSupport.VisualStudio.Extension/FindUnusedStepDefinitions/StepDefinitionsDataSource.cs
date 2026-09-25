using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Provides step-definition rows to the VS Find All References table window — the unused step
/// definitions, or the step definitions matching a step (Go To Definition, issue #757).
/// </summary>
internal sealed class StepDefinitionsDataSource : ITableDataSource
{
    private readonly IReadOnlyList<StepDefinitionListItem> _items;

    /// <summary>Creates the data source over a snapshot of step-definition rows.</summary>
    public StepDefinitionsDataSource(IReadOnlyList<StepDefinitionListItem> items)
        => _items = items;

    // ── ITableDataSource ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public string SourceTypeIdentifier => "reqnroll/stepDefinitions";
    /// <inheritdoc />
    public string Identifier           => "reqnroll.stepDefinitionSource";
    /// <inheritdoc />
    public string DisplayName          => "Reqnroll Step Definitions";

    /// <summary>Pushes all entries to <paramref name="sink"/> immediately so the window is populated synchronously on open.</summary>
    public IDisposable Subscribe(ITableDataSink sink)
    {
        var entries = _items.Select(ToEntry).Cast<ITableEntry>().ToList();
        sink.AddEntries(entries, true);
        return new SinkRegistration(sink);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StepDefinitionTableEntry ToEntry(StepDefinitionListItem item)
    {
        var entry = new StepDefinitionTableEntry();

        // DocumentName drives the File column and double-click navigation.
        // SourceFile is already an absolute path (not a URI).
        if (item.SourceFile is { Length: > 0 })
            entry.TrySetValue(StandardTableKeyNames.DocumentName, item.SourceFile);

        entry.TrySetValue(StandardTableKeyNames.Line,   item.SourceLine);  // 0-based
        entry.TrySetValue(StandardTableKeyNames.Column, item.SourceChar);  // 0-based

        // Code column: "ClassName.MethodName  ·  BindingExpression"
        //
        // Text feeds the window's fixed Code ("linetext") column. The "Project then Definition"
        // second-level grouping uses StandardTableKeyNames.Definition, whose value must be a
        // DefinitionBucket (Microsoft.VisualStudio.Shell.FindAllReferences); a plain string is
        // ignored and shows "[Definition:Unknown]". We supply none, so ClassName is embedded in
        // the Code text instead, keeping all three pieces visible in the flat or "Project then
        // File" views.
        var code = BuildCodeText(item.ClassName, item.MethodName, item.BindingExpression, item.IsResolved);
        entry.TrySetValue(StandardTableKeyNames.Text, code);

        if (item.ProjectName is { Length: > 0 })
            entry.TrySetValue(StandardTableKeyNames.ProjectName, item.ProjectName);

        return entry;
    }

    private static string BuildCodeText(string? className, string? methodName, string? expression,
        bool isResolved = true)
    {
        // "ClassName.MethodName  ·  expression"  or graceful fallback for missing parts
        var cm = className  is { Length: > 0 } ? className  : null;
        var mm = methodName is { Length: > 0 } ? methodName : null;
        var ex = expression is { Length: > 0 } ? expression : null;

        var identifier = (cm, mm) switch
        {
            (not null, not null) => $"{cm}.{mm}",
            (null,     not null) => mm,
            (not null, null)     => cm,
            _                    => "(unknown)",
        };

        var text = ex is null ? identifier : $"{identifier}  ·  {ex}";

        // A row with no DocumentName has no File column and does nothing on double-click. Saying so
        // in the one content column VS gives us is the only place a user can find out why
        // (issue #540); the recorded path itself goes to the log, since it would swamp this column.
        return isResolved ? text : $"{text}  ·  (source not on this machine — rebuild locally)";
    }

    private sealed class SinkRegistration : IDisposable
    {
        private readonly ITableDataSink _sink;
        public SinkRegistration(ITableDataSink sink) => _sink = sink;
        public void Dispose() => _sink.RemoveAllEntries();
    }
}
