using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Reqnroll.IdeSupport.Common;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

/// <summary>
/// Shared cache of parsed C# <see cref="SyntaxNode"/> roots, keyed by file path, so that repeated
/// resolutions against the same unchanged file within one logical operation (e.g. resolving Run
/// CodeLens targets for every scenario in a large <c>.feature</c> file) reuse the same Roslyn parse
/// instead of re-parsing the file from scratch on every call — see issue #491.
/// </summary>
/// <remarks>
/// Two entry points reflect the two kinds of file this cache serves:
/// <list type="bullet">
///   <item><description><see cref="GetOrParseFromDisk"/> — for files with no known live/open text,
///   e.g. a generated <c>&lt;feature&gt;.feature.cs</c> code-behind that is never opened by a
///   human. Freshness is checked via the file's last-write-time (a cheap stat call); a full
///   re-read + re-parse only happens when that changes.</description></item>
///   <item><description><see cref="GetOrParse"/> — for callers that already resolved the file's
///   current text themselves (an open editor buffer, a live-text cache). Freshness is checked by
///   comparing the given text against what was cached, so no disk I/O is needed at all.</description></item>
/// </list>
/// Both entry points are purely self-validating on read — there is no push-invalidation event to
/// wire up elsewhere; a stale entry is simply never returned; it's replaced by the next read that
/// detects the mismatch and re-parses. Bounded by a small MRU cap rather than a per-URI
/// document-lifecycle cache like <c>IDocumentBufferService</c>/<c>ICSharpFileTextCache</c>, since
/// this cache has no "close" event for disk-only files and could otherwise grow unbounded across a
/// long session touching many distinct files.
/// </remarks>
public interface ICSharpSyntaxTreeCache
{
    /// <summary>
    /// Returns the cached parse of <paramref name="filePath"/>, re-reading and re-parsing from
    /// disk only if the file's last-write-time has changed since the cached entry (or there is no
    /// cached entry yet). Returns <see langword="null"/> if the file does not exist or cannot be
    /// read.
    /// </summary>
    SyntaxNode? GetOrParseFromDisk(string filePath, IFileSystemForIDE fileSystem);

    /// <summary>
    /// Returns the cached parse of <paramref name="filePath"/> for the given <paramref name="text"/>,
    /// re-parsing only if the cached entry's text differs from <paramref name="text"/> (or there is
    /// no cached entry yet).
    /// </summary>
    SyntaxNode GetOrParse(string filePath, string text);

    /// <summary>Evicts the cached parse for <paramref name="filePath"/>, if any.</summary>
    void Invalidate(string filePath);
}

/// <summary>
/// Default in-memory implementation of <see cref="ICSharpSyntaxTreeCache"/>, keyed on file path
/// (case-insensitive) and bounded by a small most-recently-used cap.
/// </summary>
public sealed class CSharpSyntaxTreeCache : ICSharpSyntaxTreeCache
{
    // Sized for "a handful of distinct step-definition/generated files touched in one burst of
    // activity" (a rename op, a Run CodeLens pass over one large .feature file) rather than
    // "every C# file in a large solution" — this cache trades hit rate for a bounded, trivially
    // correct memory footprint.
    private const int MaxEntries = 64;

    private sealed record Entry(string Text, DateTime? LastWriteTimeUtc, SyntaxNode Root, long LastAccess);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _accessCounter;

    /// <inheritdoc/>
    public SyntaxNode? GetOrParseFromDisk(string filePath, IFileSystemForIDE fileSystem)
    {
        if (!fileSystem.File.Exists(filePath))
            return null;

        DateTime lastWriteTimeUtc;
        try
        {
            lastWriteTimeUtc = fileSystem.File.GetLastWriteTimeUtc(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (_entries.TryGetValue(filePath, out var existing) && existing.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            Touch(filePath, existing);
            return existing.Root;
        }

        string text;
        try
        {
            text = fileSystem.File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        Store(filePath, new Entry(text, lastWriteTimeUtc, root, NextAccess()));
        return root;
    }

    /// <inheritdoc/>
    public SyntaxNode GetOrParse(string filePath, string text)
    {
        if (_entries.TryGetValue(filePath, out var existing) && string.Equals(existing.Text, text, StringComparison.Ordinal))
        {
            Touch(filePath, existing);
            return existing.Root;
        }

        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        Store(filePath, new Entry(text, null, root, NextAccess()));
        return root;
    }

    /// <inheritdoc/>
    public void Invalidate(string filePath) => _entries.TryRemove(filePath, out _);

    private long NextAccess() => Interlocked.Increment(ref _accessCounter);

    private void Touch(string filePath, Entry existing) => _entries[filePath] = existing with { LastAccess = NextAccess() };

    private void Store(string filePath, Entry entry)
    {
        _entries[filePath] = entry;
        EvictIfNeeded();
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
            return;

        foreach (var key in _entries
                     .OrderBy(kvp => kvp.Value.LastAccess)
                     .Take(_entries.Count - MaxEntries)
                     .Select(kvp => kvp.Key)
                     .ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }
}
