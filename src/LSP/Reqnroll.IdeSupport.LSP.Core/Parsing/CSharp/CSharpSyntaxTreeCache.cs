using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Reqnroll.IdeSupport.Common;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

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
        // ConcurrentDictionary.ToArray() takes a moment-in-time snapshot; ordering/taking directly
        // over _entries (an IEnumerable<KeyValuePair<,>>) instead would let LINQ's internal buffering
        // fall onto the ICollection<T> CopyTo fast path, which pre-sizes its destination array from
        // a Count taken slightly before the copy — a concurrent Store growing the dictionary in that
        // window throws ArgumentException ("index is equal to or greater than the length of the
        // array"). Sorting/removing from this snapshot instead keeps the whole pass safe under
        // concurrent Store calls, matching the non-atomic-cache shape that caused issue #554.
        var snapshot = _entries.ToArray();
        if (snapshot.Length <= MaxEntries)
            return;

        foreach (var key in snapshot
                     .OrderBy(kvp => kvp.Value.LastAccess)
                     .Take(snapshot.Length - MaxEntries)
                     .Select(kvp => kvp.Key))
        {
            _entries.TryRemove(key, out _);
        }
    }
}
