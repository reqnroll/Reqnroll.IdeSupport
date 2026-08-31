using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// Provides unused step-definition locations to the VS Find All References table window.
/// </summary>
internal sealed class UnusedStepDefinitionsDataSource : ITableDataSource
{
    private readonly IReadOnlyList<UnusedStepLocation> _items;

    /// <summary>Creates the data source over a snapshot of unused step-definition locations.</summary>
    public UnusedStepDefinitionsDataSource(IReadOnlyList<UnusedStepLocation> items)
        => _items = items;

    // ── ITableDataSource ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public string SourceTypeIdentifier => "reqnroll/unusedStepDefinitions";
    /// <inheritdoc />
    public string Identifier           => "reqnroll.unusedStepDefinitionSource";
    /// <inheritdoc />
    public string DisplayName          => "Reqnroll Unused Step Definitions";

    /// <summary>Pushes all entries to <paramref name="sink"/> immediately so the window is populated synchronously on open.</summary>
    public IDisposable Subscribe(ITableDataSink sink)
    {
        var entries = _items.Select(ToEntry).Cast<ITableEntry>().ToList();
        sink.AddEntries(entries, true);
        return new SinkRegistration(sink);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static UnusedStepDefinitionTableEntry ToEntry(UnusedStepLocation item)
    {
        var entry = new UnusedStepDefinitionTableEntry();

        // DocumentName drives the File column and double-click navigation.
        // SourceFile is already an absolute path (not a URI).
        if (item.SourceFile is { Length: > 0 })
            entry.TrySetValue(StandardTableKeyNames.DocumentName, item.SourceFile);

        entry.TrySetValue(StandardTableKeyNames.Line,   item.SourceLine);  // 0-based
        entry.TrySetValue(StandardTableKeyNames.Column, item.SourceChar);  // 0-based

        // Code column: "ClassName.MethodName  ·  BindingExpression"
        //
        // The VS FAR window has a single content column (Text).  The "Project then Definition"
        // second-level grouping uses StandardTableKeyNames.Definition, which VS type-checks
        // for a Roslyn DefinitionBucket; plain strings are silently ignored and fall back to
        // "[Definition:Unknown]".  Class-level grouping is therefore not achievable for custom
        // non-Roslyn data sources.  Instead we embed ClassName in the Code column text so all
        // three pieces of information are visible in the flat or "Project then File" views.
        var code = BuildCodeText(item.ClassName, item.MethodName, item.BindingExpression, item.IsResolved);
        entry.TrySetValue(StandardTableKeyNames.Text, code);

        if (item.ProjectName is { Length: > 0 })
            entry.TrySetValue(StandardTableKeyNames.ProjectName, item.ProjectName);

        // Suppress VS's auto-generated Description column — without this, VS duplicates the
        // Code text with colour markup into a Description column (same as Find Step Definition Usages does).
        entry.TrySetValue("description", "");

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
