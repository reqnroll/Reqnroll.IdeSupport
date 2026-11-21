using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;

/// <summary>
/// What to send the client for one <c>reqnroll/semanticTokens</c> push: the complete token set, only the
/// tokens on a changed line range, or nothing because the client already holds exactly these tokens.
/// </summary>
/// <param name="Data">The LSP relative-encoded tokens to send (the full set, or just those on the changed lines).</param>
/// <param name="StartLine">First line (inclusive) the client must replace, or null when <paramref name="Data"/> is the full set.</param>
/// <param name="EndLine">Last line (inclusive) the client must replace, or null when <paramref name="Data"/> is the full set.</param>
/// <param name="TokenCount">Number of tokens in the complete set the client holds after applying this push.</param>
/// <param name="Unchanged">True when the client already holds these tokens and no notification is needed.</param>
public readonly record struct SemanticTokenPush(
    int[] Data,
    int? StartLine,
    int? EndLine,
    int TokenCount,
    bool Unchanged)
{
    /// <summary>True when only the tokens on <see cref="StartLine"/>..<see cref="EndLine"/> are carried.</summary>
    public bool IsRanged => StartLine.HasValue;
}

/// <summary>
/// Remembers the token set last sent to the client for each document, so the next push can carry only
/// what changed.
/// </summary>
/// <remarks>
/// A push is ranged against the previous push, so the client's copy must equal the baseline kept here.
/// That holds because the client applies every push it receives in order. A baseline is dropped when the
/// document closes, and the first push for a document (and every
/// <see cref="PushedSemanticTokens.FullResyncInterval"/>th push after it) is always the full set, which
/// bounds how long any divergence could last.
/// </remarks>
public interface IPushedSemanticTokens
{
    /// <summary>
    /// Compares <paramref name="encodedTokens"/> (the full LSP relative encoding of the document's
    /// current tokens) with the last set sent for <paramref name="uri"/>, records it as the new baseline
    /// and returns what to send. The caller must <see cref="Forget"/> the document if the send fails.
    /// </summary>
    /// <param name="uri">The document the tokens belong to.</param>
    /// <param name="encodedTokens">The document's current tokens, LSP relative-encoded.</param>
    /// <param name="text">
    /// The text <paramref name="encodedTokens"/> were computed from. A client that moves its copy of the
    /// tokens through each edit no longer holds exactly the last pushed set on the lines the user edited,
    /// so those lines are always part of a ranged push, even where the new tokens equal the old ones. Null
    /// when the text is not known: the plan then rests on the tokens alone.
    /// </param>
    SemanticTokenPush Plan(string uri, int[] encodedTokens, string? text = null);

    /// <summary>Drops the baseline for <paramref name="uri"/>, so the next push for it is the full set.</summary>
    void Forget(string uri);
}

/// <inheritdoc cref="IPushedSemanticTokens"/>
public sealed class PushedSemanticTokens : IPushedSemanticTokens
{
    /// <summary>Number of consecutive ranged pushes after which the next push is the full set again.</summary>
    public const int FullResyncInterval = 25;

    private readonly ConcurrentDictionary<string, Baseline> _baselines = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Baseline
    {
        public Token[] Tokens = Array.Empty<Token>();

        // The text the tokens were computed from; null when the caller did not supply it.
        public string? Text;

        // -1 until the first full push has been planned, then the ranged pushes planned since the last full one.
        public int RangedSinceFull = -1;
    }

    private readonly record struct Token(int Line, int Char, int Length, int TypeIdx, int ModBits);

    /// <inheritdoc/>
    public SemanticTokenPush Plan(string uri, int[] encodedTokens, string? text = null)
    {
        var current = DecodeAbsolute(encodedTokens);
        var baseline = _baselines.GetOrAdd(uri, _ => new Baseline());

        lock (baseline)
        {
            var previous = baseline.Tokens;
            var previousText = baseline.Text;
            var hadBaseline = baseline.RangedSinceFull >= 0;
            var resync = baseline.RangedSinceFull >= FullResyncInterval;

            baseline.Tokens = current;
            baseline.Text = text;

            // Without the text the last push was computed from there is no telling which lines the client
            // edited, so only the full set is safe.
            var textUnknown = text is not null && previousText is null;

            if (!hadBaseline || resync || textUnknown)
            {
                baseline.RangedSinceFull = 0;
                return Full(encodedTokens, current.Length);
            }

            var edited = text is not null && previousText is not null
                ? EditedLines(previousText, text)
                : null;
            return PlanRanged(baseline, previous, current, edited);
        }
    }

    /// <inheritdoc/>
    public void Forget(string uri) => _baselines.TryRemove(uri, out _);

    /// <summary>
    /// The lines of <paramref name="current"/> that differ from <paramref name="previous"/>: from the line of
    /// the first differing character to the line of the last one (a single line for a pure deletion, where
    /// the text on either side of it joins). Null when the texts are equal.
    /// </summary>
    private static (int First, int Last)? EditedLines(string previous, string current)
    {
        var prefix = previous.AsSpan().CommonPrefixLength(current);
        if (prefix == previous.Length && prefix == current.Length)
            return null;

        var limit = Math.Min(previous.Length, current.Length) - prefix;
        var suffix = 0;
        while (suffix < limit && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
            suffix++;

        var changedEnd = current.Length - suffix;
        var first = LineOf(current, prefix);
        var last = changedEnd > prefix ? LineOf(current, changedEnd - 1) : first;
        return (first, last);
    }

    /// <summary>
    /// The zero-based line of the character at <paramref name="offset"/>, counting line breaks the way the
    /// document's own line model does: <c>\n</c>, <c>\r\n</c> and a lone <c>\r</c> each end one line.
    /// </summary>
    private static int LineOf(string text, int offset)
    {
        var lines = 0;
        var position = 0;
        while (position < offset)
        {
            var found = text.AsSpan(position, offset - position).IndexOfAny('\r', '\n');
            if (found < 0)
                break;

            position += found;
            var isCarriageReturnOfPair = text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n';
            if (!isCarriageReturnOfPair)
                lines++;

            position++;
        }

        return lines;
    }

    private static SemanticTokenPush PlanRanged(
        Baseline baseline, Token[] previous, Token[] current, (int First, int Last)? edited)
    {
        // Longest common prefix and suffix of the two ordered token lists; everything between them changed.
        var limit = Math.Min(previous.Length, current.Length);
        var prefix = 0;
        while (prefix < limit && previous[prefix] == current[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < limit - prefix
               && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix])
            suffix++;

        var oldEnd = previous.Length - suffix;
        var newEnd = current.Length - suffix;

        // The client replaces whole lines, so widen the changed token span to the lines it touches in
        // either list.
        var from = int.MaxValue;
        var to = -1;
        if (prefix < oldEnd)
        {
            from = Math.Min(from, previous[prefix].Line);
            to = Math.Max(to, previous[oldEnd - 1].Line);
        }

        if (prefix < newEnd)
        {
            from = Math.Min(from, current[prefix].Line);
            to = Math.Max(to, current[newEnd - 1].Line);
        }

        // The edited lines are replaced too: the client moved its tokens through the edit, which can drop
        // or resize a token the new set contains unchanged.
        if (edited is var (editedFirst, editedLast)
            && (to >= 0 || current.Any(t => t.Line >= editedFirst && t.Line <= editedLast)))
        {
            from = Math.Min(from, editedFirst);
            to = Math.Max(to, editedLast);
        }

        if (to < 0)
            return new SemanticTokenPush(Array.Empty<int>(), null, null, current.Length, Unchanged: true);

        var slice = current
            .Where(t => t.Line >= from && t.Line <= to)
            .Select(t => (t.Line, t.Char, t.Length, t.TypeIdx, t.ModBits))
            .ToList();

        baseline.RangedSinceFull++;
        return new SemanticTokenPush(
            SemanticTokensService.DeltaEncode(slice).ToArray(), from, to, current.Length, Unchanged: false);
    }

    private static SemanticTokenPush Full(int[] encodedTokens, int tokenCount)
        => new(encodedTokens, null, null, tokenCount, Unchanged: false);

    private static Token[] DecodeAbsolute(int[] data)
    {
        var result = new Token[data.Length / 5];
        int line = 0, character = 0;
        for (int i = 0, n = 0; i + 4 < data.Length; i += 5, n++)
        {
            if (data[i] == 0)
            {
                character += data[i + 1];
            }
            else
            {
                line += data[i];
                character = data[i + 1];
            }

            result[n] = new Token(line, character, data[i + 2], data[i + 3], data[i + 4]);
        }

        return result;
    }
}
