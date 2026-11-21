using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Classification;

/// <summary>One semantic token in absolute document coordinates, with its legend type name.</summary>
internal readonly struct ClassifiedToken
{
    public ClassifiedToken(int line, int startChar, int length, string tokenType)
    {
        Line = line;
        StartChar = startChar;
        Length = length;
        TokenType = tokenType;
    }

    /// <summary>The 0-based line number of the token.</summary>
    public int Line { get; }
    /// <summary>The 0-based start character on the line.</summary>
    public int StartChar { get; }
    /// <summary>The length of the token in characters.</summary>
    public int Length { get; }

    /// <summary>The legend token-type name, e.g. <c>reqnroll.keyword</c>.</summary>
    public string TokenType { get; }
}

/// <summary>
/// One edit to a document, in line and character coordinates: the range that was replaced, in the text before the edit
/// (<see cref="OldStartLine"/>/<see cref="OldStartChar"/> to <see cref="OldEndLine"/>/<see cref="OldEndChar"/>), and
/// where the replacement text ends in the text after it. The replacement starts where the replaced range started.
/// </summary>
internal readonly struct TokenTextChange
{
    public TokenTextChange(int oldStartLine, int oldStartChar, int oldEndLine, int oldEndChar, int newEndLine, int newEndChar)
    {
        OldStartLine = oldStartLine;
        OldStartChar = oldStartChar;
        OldEndLine = oldEndLine;
        OldEndChar = oldEndChar;
        NewEndLine = newEndLine;
        NewEndChar = newEndChar;
    }

    public int OldStartLine { get; }
    public int OldStartChar { get; }
    public int OldEndLine { get; }
    public int OldEndChar { get; }
    public int NewEndLine { get; }
    public int NewEndChar { get; }

    /// <summary>How many lines the edit added (negative when it removed lines).</summary>
    public int LineCountDelta => NewEndLine - OldEndLine;
}

/// <summary>
/// Process-wide cache of the decoded LSP semantic tokens for each open <c>.feature</c> file,
/// shared between the LSP message interceptor (writer) and the editor classifier (reader).
/// </summary>
/// <remarks>
/// <para>
/// Visual Studio's built-in LSP semantic-token colorizer maps token-type names to classifications
/// through a fixed internal table, so it cannot honour Reqnroll's custom <c>reqnroll.*</c> token
/// types (they all fall back to plain "text").  To restore the custom Reqnroll colours we bypass
/// that path entirely: <see cref="SemanticTokensClassificationInterceptor"/> observes the server's
/// semantic-token responses as they flow through the connection and records them here, and
/// <see cref="GherkinSemanticClassifier"/> reads them back and produces classification spans against
/// the <c>IdeSupportClassifications</c> classification types of the same name.
/// </para>
/// <para>
/// A static singleton is used (rather than MEF) because the interceptor is constructed by the
/// non-MEF <c>ReqnrollLanguageClient</c> while the classifier is created by the editor's MEF host;
/// both need the same instance.
/// </para>
/// </remarks>
internal sealed class SemanticTokenClassificationStore
{
    public static SemanticTokenClassificationStore Instance { get; } = new SemanticTokenClassificationStore();

    private string[] _legend = Array.Empty<string>();
    private readonly ConcurrentDictionary<string, IReadOnlyList<ClassifiedToken>> _byFile =
        new ConcurrentDictionary<string, IReadOnlyList<ClassifiedToken>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised after the tokens for a file change. Arguments are the normalized file key and the first and last
    /// line (inclusive) whose tokens changed, or <c>-1, -1</c> when any line may have changed.
    /// </summary>
    /// <remarks>
    /// The store lives for the whole VS session, so a handler subscribed here stays reachable until it is
    /// removed. Per-buffer subscribers must subscribe weakly (see <see cref="GherkinSemanticClassifier"/>).
    /// </remarks>
    public event Action<string, int, int>? TokensChanged;

    /// <summary>The token-type legend advertised by the server (index → name).</summary>
    public string[] Legend => _legend;

    public void SetLegend(string[] tokenTypes) => _legend = tokenTypes ?? Array.Empty<string>();

    /// <summary>Sets the cached tokens for a file. Raises <see cref="TokensChanged"/> for the key.</summary>
    public void SetTokens(string fileKey, IReadOnlyList<ClassifiedToken> tokens)
    {
        if (fileKey is null) return;
        _byFile[fileKey] = tokens;
        TokensChanged?.Invoke(fileKey, -1, -1);
    }

    /// <summary>
    /// Replaces the cached tokens on lines <paramref name="startLine"/> to <paramref name="endLine"/> (inclusive)
    /// with <paramref name="rangeTokens"/> and keeps every token on other lines. Raises
    /// <see cref="TokensChanged"/> for that line range only. Returns false, changing nothing, when the file has no
    /// cached tokens yet, because a ranged update only makes sense on top of an earlier complete set, and for a line
    /// range that is negative or inverted.
    /// </summary>
    public bool ApplyRange(string fileKey, int startLine, int endLine, IReadOnlyList<ClassifiedToken> rangeTokens)
    {
        if (fileKey is null || startLine < 0 || endLine < startLine) return false;

        while (true)
        {
            if (!_byFile.TryGetValue(fileKey, out var existing)) return false;

            var merged = new List<ClassifiedToken>(existing.Count + rangeTokens.Count);
            var i = 0;
            for (; i < existing.Count && existing[i].Line < startLine; i++) merged.Add(existing[i]);
            merged.AddRange(rangeTokens);
            for (; i < existing.Count && existing[i].Line <= endLine; i++) { }
            for (; i < existing.Count; i++) merged.Add(existing[i]);

            // A concurrent SetTokens between the read and the write would otherwise be overwritten with a
            // set built from the older tokens; retry against the newer one instead.
            if (_byFile.TryUpdate(fileKey, merged, existing))
            {
                TokensChanged?.Invoke(fileKey, startLine, endLine);
                return true;
            }
        }
    }

    /// <summary>
    /// Moves the cached tokens of a file through edits just made to its text, so they keep describing the text they
    /// were computed for until the server's next push arrives with the real ones. A token before an edit stays; a token
    /// after it moves by what the edit added or removed (down a line when a line was added, left when a character was
    /// deleted before it on its line, and so on); a token inside the replaced text that the edit only lengthens or
    /// shortens (typing within a word) grows or shrinks with it; any other token the edit touches is dropped.
    /// Raises <see cref="TokensChanged"/> for the lines affected, to the end of the document when lines were added or
    /// removed. Returns false, changing nothing, when the file has no cached tokens or no token was affected.
    /// </summary>
    /// <param name="fileKey">The normalized file key.</param>
    /// <param name="changes">The edits of one change event, in the order they apply: later in the document first, so
    /// that the coordinates of each are not disturbed by those applied before it.</param>
    public bool ApplyTextChanges(string fileKey, IReadOnlyList<TokenTextChange> changes)
    {
        if (fileKey is null || changes is null || changes.Count == 0) return false;

        while (true)
        {
            if (!_byFile.TryGetValue(fileKey, out var existing) || existing.Count == 0) return false;

            var mapped = existing;
            foreach (var change in changes)
                mapped = MapThrough(mapped, change);

            if (ReferenceEquals(mapped, existing)) return false;

            // As in ApplyRange: do not overwrite a set that a concurrent push stored after the read above.
            if (_byFile.TryUpdate(fileKey, mapped, existing))
            {
                var firstLine = int.MaxValue;
                var lastLine = 0;
                foreach (var change in changes)
                {
                    firstLine = Math.Min(firstLine, change.OldStartLine);
                    lastLine = change.LineCountDelta != 0 ? int.MaxValue : Math.Max(lastLine, change.OldEndLine);
                    if (lastLine == int.MaxValue) break;
                }

                TokensChanged?.Invoke(fileKey, firstLine, lastLine);
                return true;
            }
        }
    }

    // Returns the same list when no token is affected.
    private static IReadOnlyList<ClassifiedToken> MapThrough(IReadOnlyList<ClassifiedToken> tokens, TokenTextChange change)
    {
        // Tokens are ordered by line: find the first one on or after the line the edit starts on.
        var low = 0;
        var high = tokens.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (tokens[mid].Line < change.OldStartLine) low = mid + 1;
            else high = mid;
        }

        var firstCandidate = low;

        // Nothing at or after the edit: nothing to move.
        var first = firstCandidate;
        while (first < tokens.Count && EndsBeforeEdit(tokens[first], change)) first++;
        if (first == tokens.Count) return tokens;

        // An edit that stays on one line and adds no line (typing after the last token of a line, say) does not move
        // the tokens of later lines, so when nothing on its own line is affected there is nothing to change: the
        // common case of typing at the end of a line costs no copy of the token set.
        var staysOnOneLine = change.OldStartLine == change.OldEndLine && change.NewEndLine == change.OldEndLine;
        if (staysOnOneLine && tokens[first].Line > change.OldStartLine) return tokens;

        var result = new List<ClassifiedToken>(tokens.Count);
        for (var i = 0; i < first; i++) result.Add(tokens[i]);

        for (var i = first; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (EndsBeforeEdit(token, change))
            {
                result.Add(token);
            }
            else if (StartsAfterEdit(token, change))
            {
                result.Add(MoveAfterEdit(token, change));
            }
            else if (TryResizeInsideEdit(token, change, out var resized))
            {
                result.Add(resized);
            }
            // otherwise the edit cut into the token in a way that leaves nothing reliable to show: drop it
        }

        return result;
    }

    private static bool EndsBeforeEdit(ClassifiedToken token, TokenTextChange change) =>
        token.Line < change.OldStartLine
        || (token.Line == change.OldStartLine && token.StartChar + token.Length <= change.OldStartChar);

    private static bool StartsAfterEdit(ClassifiedToken token, TokenTextChange change) =>
        token.Line > change.OldEndLine
        || (token.Line == change.OldEndLine && token.StartChar >= change.OldEndChar);

    private static ClassifiedToken MoveAfterEdit(ClassifiedToken token, TokenTextChange change)
    {
        if (token.Line == change.OldEndLine)
        {
            // On the line where the replaced text ended: the token follows the replacement text.
            return new ClassifiedToken(
                change.NewEndLine, change.NewEndChar + (token.StartChar - change.OldEndChar), token.Length, token.TokenType);
        }

        return new ClassifiedToken(token.Line + change.LineCountDelta, token.StartChar, token.Length, token.TokenType);
    }

    // A single-line edit within a token (typing in a word, deleting part of one) changes how long the token is.
    private static bool TryResizeInsideEdit(ClassifiedToken token, TokenTextChange change, out ClassifiedToken resized)
    {
        resized = default;
        var withinOneLine = change.OldStartLine == change.OldEndLine && change.NewEndLine == change.OldStartLine;
        if (!withinOneLine || token.Line != change.OldStartLine) return false;
        if (token.StartChar > change.OldStartChar || change.OldEndChar > token.StartChar + token.Length) return false;

        var newLength = token.Length + (change.NewEndChar - change.OldStartChar) - (change.OldEndChar - change.OldStartChar);
        if (newLength <= 0) return false;

        resized = new ClassifiedToken(token.Line, token.StartChar, newLength, token.TokenType);
        return true;
    }

    /// <summary>Gets the cached tokens for a file, if any have been stored.</summary>
    public bool TryGetTokens(string fileKey, out IReadOnlyList<ClassifiedToken> tokens)
    {
        if (fileKey is not null && _byFile.TryGetValue(fileKey, out var found))
        {
            tokens = found;
            return true;
        }

        tokens = Array.Empty<ClassifiedToken>();
        return false;
    }

    /// <summary>
    /// Normalizes either a <c>file://</c> URI or a local path to a stable comparison key
    /// (absolute, lower-cased) so the interceptor (which sees URIs) and the classifier
    /// (which sees file paths) agree.
    /// </summary>
    public static string? NormalizeKey(string? pathOrUri)
    {
        if (string.IsNullOrEmpty(pathOrUri)) return null;
        try
        {
            string? path = Uri.TryCreate(pathOrUri, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : pathOrUri;
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return pathOrUri!.ToLowerInvariant();
        }
    }
}