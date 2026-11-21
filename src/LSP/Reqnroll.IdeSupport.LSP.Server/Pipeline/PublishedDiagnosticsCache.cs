using System.Security.Cryptography;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Pipeline;

/// <inheritdoc cref="IPublishedDiagnosticsCache"/>
/// <remarks>
/// The signature is order-insensitive: clients render the set by position, so a pure reordering
/// shows nothing new and is not worth a push. It is a multiset, so a duplicated diagnostic still
/// counts. It covers every field a client can display or act on: range, severity, code, code
/// description, source, message, tags, related information and data.
/// </remarks>
public sealed class PublishedDiagnosticsCache : IPublishedDiagnosticsCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _tableLock = new();

    private sealed class Entry
    {
        /// <summary>Signature of the last set sent; null when nothing has been sent since the entry was created.</summary>
        public string? Signature;

        /// <summary>Set once the entry has left the table, so a caller still holding it looks the URI up again.</summary>
        public bool Removed;
    }

    /// <inheritdoc/>
    public bool TryPublish(DocumentUri uri, IReadOnlyCollection<Diagnostic> diagnostics, Func<bool> isOpen, Action publish)
    {
        var key = uri.ToString();
        var signature = ComputeSignature(diagnostics);

        while (true)
        {
            Entry entry;
            lock (_tableLock)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    if (!isOpen())
                        return false;
                    entry = new Entry();
                    _entries[key] = entry;
                }
            }

            lock (entry)
            {
                if (entry.Removed)
                    continue;

                // A close removes the buffer before it evicts, so once the buffer is gone no new
                // set may be recorded for this URI.
                if (!isOpen())
                    return false;

                if (string.Equals(entry.Signature, signature, StringComparison.Ordinal))
                    return false;

                publish();
                entry.Signature = signature;
                return true;
            }
        }
    }

    /// <inheritdoc/>
    public void Reset(DocumentUri uri) => Evict(uri);

    /// <inheritdoc/>
    public void Evict(DocumentUri uri)
    {
        Entry? entry;
        lock (_tableLock)
        {
            if (!_entries.Remove(uri.ToString(), out entry))
                return;
        }

        // Taking the entry lock waits for an in-flight publish to finish sending, so a follow-up
        // notification from the caller (the clear on close) is sent after it.
        lock (entry)
            entry.Removed = true;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        Entry[] entries;
        lock (_tableLock)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            lock (entry)
                entry.Removed = true;
        }
    }

    internal static string ComputeSignature(IReadOnlyCollection<Diagnostic> diagnostics)
    {
        var parts = diagnostics.Select(Describe).ToArray();
        Array.Sort(parts, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append(parts.Length).Append('|');
        foreach (var part in parts)
            Append(sb, part);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Describe(Diagnostic d)
    {
        var sb = new StringBuilder();
        AppendRange(sb, d.Range);
        Append(sb, d.Severity?.ToString());
        Append(sb, d.Code is { } code ? (code.IsLong ? "n" + code.Long : "s" + code.String) : null);
        Append(sb, d.CodeDescription?.Href.ToString());
        Append(sb, d.Source);
        Append(sb, d.Message);

        var tags = (d.Tags ?? Enumerable.Empty<DiagnosticTag>()).Select(t => t.ToString()).OrderBy(t => t, StringComparer.Ordinal);
        Append(sb, string.Join(",", tags));

        var related = (d.RelatedInformation ?? Enumerable.Empty<DiagnosticRelatedInformation>())
            .Select(DescribeRelated).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        sb.Append(related.Length).Append(':');
        foreach (var r in related)
            Append(sb, r);

        Append(sb, d.Data?.ToString());
        return sb.ToString();
    }

    private static string DescribeRelated(DiagnosticRelatedInformation r)
    {
        var sb = new StringBuilder();
        Append(sb, r.Location.Uri.ToString());
        AppendRange(sb, r.Location.Range);
        Append(sb, r.Message);
        return sb.ToString();
    }

    private static void AppendRange(StringBuilder sb, LspRange? r)
    {
        if (r is null)
        {
            sb.Append("-;");
            return;
        }

        sb.Append(r.Start.Line).Append(',').Append(r.Start.Character).Append(',')
          .Append(r.End.Line).Append(',').Append(r.End.Character).Append(';');
    }

    // Length-prefixed so field boundaries are unambiguous whatever the text contains; null differs from "".
    private static void Append(StringBuilder sb, string? value)
    {
        if (value is null)
            sb.Append("~;");
        else
            sb.Append(value.Length).Append(':').Append(value).Append(';');
    }
}
