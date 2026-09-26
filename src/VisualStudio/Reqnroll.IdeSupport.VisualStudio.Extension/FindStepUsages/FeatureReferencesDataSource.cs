using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// Provides step-usage locations to the VS Find All References table window.
/// Constructed with a snapshot of locations; calling <see cref="Subscribe"/> pushes all
/// entries immediately so the window is populated synchronously on open.
/// </summary>
internal sealed class FeatureReferencesDataSource : ITableDataSource
{
    private readonly IReadOnlyList<StepUsageLocation> _locations;

    /// <summary>Creates a data source with the list of step-usage locations to display.</summary>
    public FeatureReferencesDataSource(IReadOnlyList<StepUsageLocation> locations)
    {
        _locations = locations;
    }

    // ── ITableDataSource ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public string SourceTypeIdentifier => "reqnroll/findReferences";
    /// <inheritdoc />
    public string Identifier           => "reqnroll.featureReferenceSource";
    /// <inheritdoc />
    public string DisplayName          => "Reqnroll Feature References";

    public IDisposable Subscribe(ITableDataSink sink)
    {
        // Cache file contents so multiple references in the same feature file read it once.
        var lineCache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        var entries = _locations.Select(loc => (ITableEntry)ToEntry(loc, lineCache)).ToList();
        sink.AddEntries(entries, true);
        return new SinkRegistration(sink);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FeatureReferenceTableEntry ToEntry(
        StepUsageLocation loc,
        Dictionary<string, string[]?> lineCache)
    {
        // Convert the LSP document URI (e.g. "file:///C:/project/calc.feature") to a
        // file-system path so VS can navigate to it on double-click.
        string filePath;
        if (Uri.TryCreate(loc.FileUri, UriKind.Absolute, out var uri))
            filePath = uri.LocalPath;
        else
            filePath = loc.FileUri;

        // Prefer the step text supplied by the server (extracted from the in-memory snapshot at
        // parse time, no disk I/O).  Fall back to reading the saved file from disk when the server
        // did not supply text (e.g. when talking to an older server version).
        var stepText = (loc.StepText is { Length: > 0 })
            ? loc.StepText
            : ReadStepText(filePath, loc.StartLine, lineCache);

        // Build the Code column value: "ScenarioName: Keyword stepText"
        // e.g. "Add two numbers: Given the first number is 50"
        var codeText = BuildCodeText(loc.ScenarioName, loc.Keyword, stepText);

        var entry = new FeatureReferenceTableEntry();
        // DocumentName drives the File column and double-click navigation (with Line/Column).
        entry.TrySetValue(StandardTableKeyNames.DocumentName, filePath);
        entry.TrySetValue(StandardTableKeyNames.Line,         loc.StartLine);  // 0-based
        entry.TrySetValue(StandardTableKeyNames.Column,       loc.StartChar);  // 0-based
        // Text feeds the window's fixed "Code" (linetext) column.
        entry.TrySetValue(StandardTableKeyNames.Text,         codeText);
        // Project column.
        if (loc.ProjectName is { Length: > 0 })
            entry.TrySetValue(StandardTableKeyNames.ProjectName, loc.ProjectName);
        return entry;
    }

    /// <summary>
    /// Builds the Code-column display text in the form
    /// <c>"{ScenarioName}: {Keyword} {stepText}"</c>.
    /// Omits the scenario prefix for Background steps (no scenario name).
    /// </summary>
    private static string BuildCodeText(string? scenarioName, string? keyword, string stepText)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(scenarioName))
        {
            sb.Append(scenarioName);
            sb.Append(": ");
        }
        if (!string.IsNullOrEmpty(keyword))
        {
            sb.Append(keyword);
            sb.Append(' ');
        }
        sb.Append(stepText);
        return sb.ToString();
    }

    /// <summary>
    /// Returns the trimmed text of the feature-file line at <paramref name="line0"/> (0-based),
    /// reading the file once via <paramref name="lineCache"/>.  Falls back to the file name if the
    /// file can't be read or the line is out of range (e.g. the saved file differs from an unsaved
    /// in-editor buffer).
    /// </summary>
    private static string ReadStepText(string filePath, int line0, Dictionary<string, string[]?> lineCache)
    {
        if (!lineCache.TryGetValue(filePath, out var lines))
        {
            try { lines = File.ReadAllLines(filePath); }
            catch { lines = null; }
            lineCache[filePath] = lines;
        }

        if (lines is not null && line0 >= 0 && line0 < lines.Length)
        {
            var text = lines[line0].Trim();
            if (text.Length > 0)
                return text;
        }

        return Path.GetFileName(filePath);
    }

    // Removes all entries when the window discards the source.
    private sealed class SinkRegistration : IDisposable
    {
        private readonly ITableDataSink _sink;
        public SinkRegistration(ITableDataSink sink) => _sink = sink;
        public void Dispose() => _sink.RemoveAllEntries();
    }
}
