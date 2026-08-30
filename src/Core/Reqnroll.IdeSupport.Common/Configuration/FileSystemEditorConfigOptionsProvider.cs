using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Reads <c>.editorconfig</c> files from the file system and resolves the merged settings
/// that apply to a given document path, following the standard EditorConfig upward-search
/// semantics (<c>root = true</c> stops the walk).
/// <para>
/// Parsed <c>.editorconfig</c> files are cached by path and last-write time. Call
/// <see cref="InvalidateCache"/> when a file changes so the next lookup re-reads it.
/// </para>
/// </summary>
public sealed class FileSystemEditorConfigOptionsProvider : IEditorConfigOptionsProvider
{
    private readonly IFileSystem _fileSystem;

    private readonly ConcurrentDictionary<string, CachedEditorConfig> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="FileSystemEditorConfigOptionsProvider"/> class.</summary>
    public FileSystemEditorConfigOptionsProvider(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    // ── IEditorConfigOptionsProvider ─────────────────────────────────────────

    /// <summary>Resolves the merged EditorConfig settings that apply to <paramref name="filePath"/> by walking up its directory tree.</summary>
    public IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath)
    {
        var files = CollectEditorConfigFiles(Path.GetFullPath(filePath));
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Apply most-distant first so closer files override
        foreach (var (ecPath, parsed) in files)
        {
            var ecDir = Path.GetDirectoryName(ecPath)!;
            foreach (var section in parsed.Sections)
            {
                if (SectionApplies(section.Pattern, ecDir, filePath))
                    foreach (var kv in section.Values)
                        merged[kv.Key] = kv.Value;
            }
        }

        return new FileSystemEditorConfigOptions(merged);
    }

    /// <summary>Evicts the cached parse result for the given <c>.editorconfig</c> file so the next lookup re-reads it from disk.</summary>
    public void InvalidateCache(string editorConfigFilePath)
    {
        _cache.TryRemove(editorConfigFilePath, out _);
    }

    // ── File discovery ────────────────────────────────────────────────────────

    private List<(string Path, ParsedEditorConfig Parsed)> CollectEditorConfigFiles(string targetFilePath)
    {
        var collected = new List<(string, ParsedEditorConfig)>();
        var dir = Path.GetDirectoryName(targetFilePath);

        while (dir is not null)
        {
            var ecPath = Path.Combine(dir, ".editorconfig");
            if (_fileSystem.File.Exists(ecPath))
            {
                var parsed = GetOrParse(ecPath);
                collected.Add((ecPath, parsed));
                if (parsed.IsRoot) break;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break; // reached drive root
            dir = parent;
        }

        collected.Reverse(); // most-distant first
        return collected;
    }

    // ── Parsing and caching ───────────────────────────────────────────────────

    private ParsedEditorConfig GetOrParse(string ecPath)
    {
        var lastWrite = _fileSystem.File.GetLastWriteTimeUtc(ecPath);

        if (_cache.TryGetValue(ecPath, out var cached) && cached.LastWriteTime == lastWrite)
            return cached.Parsed;

        var content = _fileSystem.File.ReadAllText(ecPath);
        var parsed = Parse(content);
        _cache[ecPath] = new CachedEditorConfig(lastWrite, parsed);
        return parsed;
    }

    private static ParsedEditorConfig Parse(string content)
    {
        bool isRoot = false;
        var sections = new List<EditorConfigSection>();
        string? currentPattern = null;
        var currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            if (line[0] == '[')
            {
                if (currentPattern is not null)
                    sections.Add(new EditorConfigSection(currentPattern, currentValues));

                var end = line.LastIndexOf(']');
                currentPattern = end > 1 ? line.Substring(1, end - 1).Trim() : null;
                currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var value = line.Substring(eq + 1).Trim();

            if (currentPattern is null)
            {
                if (key == "root" && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    isRoot = true;
            }
            else
            {
                currentValues[key] = value;
            }
        }

        if (currentPattern is not null)
            sections.Add(new EditorConfigSection(currentPattern, currentValues));

        return new ParsedEditorConfig(isRoot, sections);
    }

    // ── Section glob matching ─────────────────────────────────────────────────

    private static bool SectionApplies(string pattern, string editorConfigDir, string targetFilePath)
    {
        var normalized = NormalizePattern(pattern);
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(normalized);

        // Match the target path relative to the .editorconfig's directory
        var relative = GetRelativePath(editorConfigDir, targetFilePath)
                           .Replace(Path.DirectorySeparatorChar, '/');

        return matcher.Match(relative).HasMatches;
    }

    /// <summary>
    /// Applies EditorConfig glob-normalization rules:
    /// patterns without a path separator are treated as matching in any subdirectory
    /// (equivalent to prefixing with <c>**/</c>).
    /// Patterns starting with <c>/</c> are anchored to the .editorconfig directory.
    /// </summary>
    private static string NormalizePattern(string pattern)
    {
        if (pattern.StartsWith("/"))
            return pattern.TrimStart('/');
        if (!pattern.Contains('/') && !pattern.Contains('\\'))
            return "**/" + pattern;
        return pattern;
    }

    /// <summary>
    /// netstandard2.0-safe substitute for <c>Path.GetRelativePath</c> (added in .NET Standard 2.1,
    /// not part of this project's target). <paramref name="targetPath"/> is always an ancestor-
    /// relative path under <paramref name="basePath"/> here — both come from the upward directory
    /// walk in <see cref="CollectEditorConfigFiles"/> — so a simple prefix strip is sufficient.
    /// </summary>
    private static string GetRelativePath(string basePath, string targetPath)
    {
        var normalizedBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (targetPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            var relative = targetPath.Substring(normalizedBase.Length);
            return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return targetPath;
    }

    // ── Private data types ────────────────────────────────────────────────────

    private readonly record struct CachedEditorConfig(DateTime LastWriteTime, ParsedEditorConfig Parsed);
    private readonly record struct ParsedEditorConfig(bool IsRoot, List<EditorConfigSection> Sections);
    private readonly record struct EditorConfigSection(string Pattern, Dictionary<string, string> Values);
}
