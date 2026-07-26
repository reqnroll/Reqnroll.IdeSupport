using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;



namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// Maps <see cref="DeveroomTag"/> instances to LSP semantic token integer tuples
/// on demand and caches the encoded result per document version.
/// Encoding is deferred until the client sends a semantic tokens request.
/// The token type legend and the tag→token mapping are the fixed Reqnroll definitions
/// in <see cref="ReqnrollSemanticTokens"/> (identical for every IDE client).
/// </summary>
public sealed class SemanticTokenService : ISemanticTokenService
{
    // ── State ─────────────────────────────────────────────────────────────────
    private readonly IDocumentBufferService _documentBufferService;
    private readonly IIdeSupportLogger         _logger;

    // key: (uri, version)  value: encoded token data
    private readonly ConcurrentDictionary<(DocumentUri, int), global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens> _cache = new();

    /// <summary>
    /// Shared empty token set — returned instead of null so that OmniSharp's
    /// DelegatingRequestHandler never receives a null response (which throws
    /// ArgumentNullException from JToken.FromObject(null)).
    /// </summary>
    private static readonly global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens EmptyTokens = new() { Data = [] };

    // ── ISemanticTokenService.Legend ──────────────────────────────────────────
    /// <inheritdoc/>
    public SemanticTokensLegend Legend => ReqnrollSemanticTokens.Legend;

    // ── Construction ──────────────────────────────────────────────────────────
    /// <summary>Initializes a new instance of the <see cref="SemanticTokenService"/> class.</summary>
    public SemanticTokenService(
        IDocumentBufferService documentBufferService,
        IIdeSupportLogger         logger)
    {
        _documentBufferService = documentBufferService;
        _logger                = logger;
    }

    // ── ISemanticTokenService ─────────────────────────────────────────────────
    /// <summary>Returns the semantic tokens for the given document/version, computing and caching them on a cache miss.</summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensAsync(
        DocumentUri uri, int version, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue((uri, version), out var tokens))
        {
            _logger.LogVerbose($"SemanticTokenService: cache hit for {uri} v{version}");
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(tokens);
        }

        // Cache miss – encode from the tags already stored in the document buffer.
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer?.Tags is not { } tags || tags.Count == 0)
        {
            _logger.LogVerbose($"SemanticTokenService: no tags available for {uri} v{version}");
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(EmptyTokens);
        }

        var encoded = Encode(tags);
        tokens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = [.. encoded], ResultId = $"{uri}@{version}" };
        _cache[(uri, version)] = tokens;
        PurgePriorVersions(uri, version);
        _logger.LogInfo($"SemanticTokenService: encoded {encoded.Count / 5} tokens for {uri} v{version}");
        return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(tokens);
    }

    /// <summary>
    /// Evicts the cached token result for <paramref name="uri"/> so that the next
    /// <see cref="GetSemanticTokensAsync"/> call re-encodes from the current tags.
    /// Must be called whenever <see cref="IDocumentBufferService.UpdateTags"/> stores
    /// a new tag set for a document whose version has not changed (e.g. after binding
    /// discovery completes for an already-open file).
    /// </summary>
    public void InvalidateCache(DocumentUri uri)
    {
        foreach (var key in _cache.Keys.Where(k => k.Item1 == uri))
            _cache.TryRemove(key, out _);
    }

    // ── Encoding ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Converts a flat collection of <see cref="DeveroomTag"/> instances into the
    /// LSP semantic token integer encoding (5 ints per token):
    /// deltaLine, deltaStartChar, length, tokenTypeIndex, tokenModifierBitset.
    /// Only leaf-level tags that map to a token type are emitted; container
    /// block tags (FeatureBlock, etc.) are not emitted themselves but their
    /// children are processed recursively.
    /// </summary>
    private static List<int> Encode(IReadOnlyCollection<DeveroomTag> tags)
    {
        // Collect all leaf tokens in document order (line asc, char asc).
        var entries = new List<(int Line, int Char, int Length, int TypeIdx, int ModBits)>();
        CollectLeafTokens(tags, entries);

        // Primary sort: (line, char) ascending.
        // Tie-break: length descending so that a longer outer token (e.g. DefinedStep
        // spanning the full step text) always sorts BEFORE a shorter inner token
        // (e.g. StepParameter) when both start at the same position.  Without this,
        // List<T>.Sort — which is not stable — can place the inner token first, causing
        // the split algorithm below to treat the outer token as "contained" inside the
        // inner one and produce inverted, nonsensical output.
        entries.Sort((a, b) =>
        {
            int c = a.Line.CompareTo(b.Line);
            if (c != 0) return c;
            c = a.Char.CompareTo(b.Char);
            if (c != 0) return c;
            return b.Length.CompareTo(a.Length); // longer first
        });

        entries = ResolveOverlaps(entries);

        var result = new List<int>(entries.Count * 5);
        int prevLine = 0, prevChar = 0;

        foreach (var (line, ch, length, type, modifiers) in entries)
        {
            int deltaLine = line - prevLine;
            int deltaChar = deltaLine == 0 ? ch - prevChar : ch;

            result.Add(deltaLine);
            result.Add(deltaChar);
            result.Add(length);
            result.Add(type);
            result.Add(modifiers);

            prevLine = line;
            prevChar = ch;
        }

        return result;
    }

    /// <summary>
    /// Resolves overlapping tokens (LSP spec §3.16: tokens must not overlap) in a list already
    /// sorted by (Line, Char) ascending, then Length descending at equal positions (a longer
    /// outer token must sort before a shorter one it contains — <see cref="List{T}.Sort"/> is
    /// not stable, so an inverted sort would make this method treat the outer token as
    /// contained inside the inner one and produce inverted, nonsensical output).
    /// <para>
    /// The canonical case is DefinedStep (function, spans full step text) containing
    /// one or more StepParameter (parameter) tokens within its span:
    /// </para>
    /// <para>
    ///   DefinedStep:   "I enter {string} as the username"   col 7..40<br/>
    ///   StepParameter: "admin"                              col 15..20
    /// </para>
    /// <para>
    /// Simple trimming (end the function token before the parameter) only works when
    /// the parameter is the LAST thing in the span. When the parameter is in the
    /// middle — or there are multiple parameters — the remaining text after the
    /// parameter(s) would be left uncoloured.
    /// </para>
    /// <para>
    /// Algorithm: for each entry that has one or more later entries on the same line
    /// whose start falls strictly within its span, replace it with:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>a function-typed gap token for each non-parameter segment (if len &gt; 0)</description></item>
    ///   <item><description>the contained token(s) in place</description></item>
    /// </list>
    /// <para>
    /// Because the list is sorted by (line, char), contained entries appear
    /// immediately after their container; the outer while-loop advances 'idx' past
    /// all entries consumed in each iteration, so each entry is emitted exactly once.
    /// </para>
    /// </summary>
    internal static List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> ResolveOverlaps(
        List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> sortedEntries)
    {
        var resolved = new List<(int Line, int Char, int Length, int TypeIdx, int ModBits)>(
            sortedEntries.Count * 2);
        int idx = 0;
        while (idx < sortedEntries.Count)
        {
            var (line, ch, len, type, mods) = sortedEntries[idx];
            int spanEnd = ch + len;

            // Count how many subsequent entries on the same line start inside this span.
            int innerCount = 0;
            for (int k = idx + 1; k < sortedEntries.Count; k++)
            {
                var (kLine, kCh, _, _, _) = sortedEntries[k];
                if (kLine != line || kCh >= spanEnd) break;
                innerCount++;
            }

            if (innerCount == 0)
            {
                resolved.Add((line, ch, len, type, mods));
                idx++;
            }
            else
            {
                // Split the outer token around each inner token.
                int cursor = ch;
                for (int k = idx + 1; k <= idx + innerCount; k++)
                {
                    var (_, innerCh, innerLen, innerType, innerMods) = sortedEntries[k];

                    // Gap before this inner token (may be zero-length at the very start).
                    if (innerCh > cursor)
                        resolved.Add((line, cursor, innerCh - cursor, type, mods));

                    // The inner token itself.
                    resolved.Add((line, innerCh, innerLen, innerType, innerMods));
                    cursor = innerCh + innerLen;
                }

                // Trailing gap after the last inner token.
                if (cursor < spanEnd)
                    resolved.Add((line, cursor, spanEnd - cursor, type, mods));

                // Advance past the outer entry and all consumed inner entries.
                idx += 1 + innerCount;
            }
        }
        return resolved;
    }

    private static void CollectLeafTokens(
        IEnumerable<DeveroomTag> tags,
        List<(int Line, int Char, int Length, int TypeIdx, int ModBits)> entries)
    {
        foreach (var tag in tags)
        {
            if (ReqnrollSemanticTokens.TryGetToken(tag, out var typeIdx, out var modBits))
            {
                var (startLine, startChar) = ResolvePosition(tag.Range, tag.Range.Start);
                var (endLine, endChar) = ResolvePosition(tag.Range, tag.Range.End);

                // For multi-line tokens emit one entry per line.
                if (startLine == endLine)
                {
                    int length = endChar - startChar;
                    if (length > 0)
                        entries.Add((startLine, startChar, length, typeIdx, modBits));
                }
                else
                {
                    // First line: from startChar to end of line
                    var firstLine = tag.Range.Snapshot.GetLineFromLineNumber(startLine);
                    int firstLineLength = firstLine.End - firstLine.Start - startChar;
                    if (firstLineLength > 0)
                        entries.Add((startLine, startChar, firstLineLength, typeIdx, modBits));

                    // Middle lines
                    for (int ln = startLine + 1; ln < endLine; ln++)
                    {
                        var midLine = tag.Range.Snapshot.GetLineFromLineNumber(ln);
                        int midLength = midLine.End - midLine.Start;
                        if (midLength > 0)
                            entries.Add((ln, 0, midLength, typeIdx, modBits));
                    }

                    // Last line: from column 0 to endChar
                    if (endChar > 0)
                        entries.Add((endLine, 0, endChar, typeIdx, modBits));
                }
            }

            // Do NOT recurse into ChildTags – the flat collection passed from
            // GherkinDocumentTaggerService already contains all descendants
            // (DeveroomTagParser.GetAllTags flattens the tree before caching).
        }
    }

    /// <summary>
    /// Resolves an absolute character offset within a snapshot to (line, character).
    /// </summary>
    private static (int Line, int Character) ResolvePosition(GherkinRange range, int absoluteOffset)
    {
        var snapshot = range.Snapshot;
        // Linear scan — acceptable for typical feature file sizes.
        for (int ln = 0; ln < snapshot.LineCount; ln++)
        {
            var line = snapshot.GetLineFromLineNumber(ln);
            if (absoluteOffset <= line.End)
                return (ln, absoluteOffset - line.Start);
        }
        // Clamp to end of last line.
        int lastLine = snapshot.LineCount - 1;
        var last = snapshot.GetLineFromLineNumber(lastLine);
        return (lastLine, last.End - last.Start);
    }

    // ── Cache housekeeping ────────────────────────────────────────────────────
    private void PurgePriorVersions(DocumentUri uri, int currentVersion)
    {
        foreach (var key in _cache.Keys)
        {
            if (key.Item1 == uri && key.Item2 < currentVersion)
                _cache.TryRemove(key, out _);
        }
    }
}
