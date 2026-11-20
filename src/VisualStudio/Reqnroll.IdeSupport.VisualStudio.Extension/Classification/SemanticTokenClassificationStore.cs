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