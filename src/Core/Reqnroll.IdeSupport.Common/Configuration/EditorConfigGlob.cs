using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Matches <c>.editorconfig</c> section names against file paths using the EditorConfig glob
/// semantics (<see href="https://spec.editorconfig.org/#glob-expressions"/>): <c>*</c> (any
/// characters except <c>/</c>), <c>**</c> (any characters), <c>?</c> (one character except
/// <c>/</c>), <c>[seq]</c> / <c>[!seq]</c>, <c>{s1,s2,s3}</c>, <c>{num1..num2}</c> and
/// backslash escapes. A pattern without <c>/</c> matches the file name at any depth; a pattern
/// with <c>/</c> is relative to the directory of the <c>.editorconfig</c> file.
/// <para>
/// Each pattern is translated once into a <see cref="Regex"/> and cached. Matching is
/// case-insensitive on Windows (as the file system is) and case-sensitive elsewhere.
/// </para>
/// </summary>
internal static class EditorConfigGlob
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly RegexOptions Options =
        RegexOptions.CultureInvariant |
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? RegexOptions.IgnoreCase : RegexOptions.None);

    private static readonly Regex NumericRange = new(@"^([+-]?\d+)\.\.([+-]?\d+)$", RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, CompiledGlob> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="pattern"/> (a section name, without the
    /// surrounding brackets) applies to <paramref name="relativePath"/>, a <c>/</c>-separated path
    /// relative to the <c>.editorconfig</c> file's directory.
    /// </summary>
    public static bool IsMatch(string pattern, string relativePath)
    {
        var glob = Cache.GetOrAdd(pattern, Compile);
        if (glob.Regex is null) return false;

        try
        {
            var match = glob.Regex.Match(relativePath);
            if (!match.Success) return false;

            for (int i = 0; i < glob.Ranges.Count; i++)
            {
                var group = match.Groups[RangeGroupName(i)];
                if (!group.Success) continue; // range sits in an alternative that did not match

                var (min, max) = glob.Ranges[i];
                if (!long.TryParse(group.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                    value < min || value > max)
                    return false;
            }

            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static CompiledGlob Compile(string pattern)
    {
        // A pattern with no '/' matches at any depth; one with '/' is anchored to the .editorconfig directory.
        string normalized;
        if (pattern.IndexOf('/') < 0)
            normalized = "**/" + pattern;
        else if (pattern.StartsWith("/", StringComparison.Ordinal))
            normalized = pattern.Substring(1);
        else
            normalized = pattern;

        var ranges = new List<(long Min, long Max)>();
        var body = Translate(normalized, ranges);

        try
        {
            return new CompiledGlob(new Regex("^" + body + "$", Options, MatchTimeout), ranges);
        }
        catch (ArgumentException)
        {
            // e.g. a reversed character range such as [z-a]; such a section never applies.
            return new CompiledGlob(null, ranges);
        }
    }

    private static string Translate(string glob, List<(long Min, long Max)> ranges)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < glob.Length)
                    {
                        sb.Append(Regex.Escape(glob[i + 1].ToString()));
                        i += 2;
                    }
                    else
                    {
                        sb.Append(@"\\");
                        i++;
                    }
                    break;

                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        bool atSegmentStart = i == 0 || glob[i - 1] == '/';
                        if (atSegmentStart && i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            // "**/" spans zero or more whole directories
                            sb.Append("(?:.*/)?");
                            i += 3;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i++;
                    }
                    break;

                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;

                case '[':
                    i = TranslateBracket(glob, i, sb);
                    break;

                case '{':
                    i = TranslateBrace(glob, i, sb, ranges);
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Translates <c>[seq]</c> / <c>[!seq]</c> starting at <paramref name="start"/>; returns the index after it.</summary>
    private static int TranslateBracket(string glob, int start, StringBuilder sb)
    {
        int end = FindClosing(glob, start + 1, ']');
        var content = end < 0 ? null : glob.Substring(start + 1, end - start - 1);
        if (content is null || content.Length == 0 || content.IndexOf('/') >= 0)
        {
            // Unterminated, empty, or spanning a path separator: the '[' is literal.
            sb.Append(@"\[");
            return start + 1;
        }

        bool negate = content[0] == '!' || content[0] == '^';
        if (negate) content = content.Substring(1);

        sb.Append(negate ? "[^/" : "[");
        for (int j = 0; j < content.Length; j++)
        {
            char c = content[j];
            if (c == '\\' && j + 1 < content.Length)
                c = content[++j];
            else if (c == '-')
            {
                // A leading or trailing '-' is literal; elsewhere it forms a range.
                sb.Append(j == 0 || j == content.Length - 1 ? @"\-" : "-");
                continue;
            }

            // Only these are special inside a .NET character class ('-' ranges are handled above).
            if (c is '\\' or ']' or '[' or '^')
                sb.Append('\\');
            sb.Append(c);
        }
        sb.Append(']');
        return end + 1;
    }

    /// <summary>Translates <c>{s1,s2}</c> / <c>{num1..num2}</c> starting at <paramref name="start"/>; returns the index after it.</summary>
    private static int TranslateBrace(string glob, int start, StringBuilder sb, List<(long Min, long Max)> ranges)
    {
        int end = FindClosing(glob, start + 1, '}');
        if (end < 0)
        {
            sb.Append(@"\{");
            return start + 1;
        }

        var content = glob.Substring(start + 1, end - start - 1);

        var range = NumericRange.Match(content);
        if (range.Success &&
            long.TryParse(range.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var a) &&
            long.TryParse(range.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b))
        {
            sb.Append("(?<").Append(RangeGroupName(ranges.Count)).Append(@">[+-]?\d+)");
            ranges.Add((Math.Min(a, b), Math.Max(a, b)));
            return end + 1;
        }

        var alternatives = SplitTopLevel(content);
        if (alternatives.Count < 2)
        {
            // "{single}" has no alternatives: the braces are literal.
            sb.Append(@"\{").Append(Translate(content, ranges)).Append(@"\}");
            return end + 1;
        }

        sb.Append("(?:");
        for (int k = 0; k < alternatives.Count; k++)
        {
            if (k > 0) sb.Append('|');
            sb.Append(Translate(alternatives[k], ranges));
        }
        sb.Append(')');
        return end + 1;
    }

    /// <summary>
    /// Finds the index of the <paramref name="close"/> character that ends a group opened just
    /// before <paramref name="from"/>, skipping escaped characters and (for braces) nested groups.
    /// Returns -1 when the group is unterminated.
    /// </summary>
    private static int FindClosing(string glob, int from, char close)
    {
        int depth = 0;
        for (int i = from; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '\\') { i++; continue; }
            if (close == '}' && c == '{') { depth++; continue; }
            if (c == close)
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }

    /// <summary>Splits brace content on commas that are not escaped or inside a nested brace group.</summary>
    private static List<string> SplitTopLevel(string content)
    {
        var parts = new List<string>();
        int depth = 0, last = 0;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(content.Substring(last, i - last));
                last = i + 1;
            }
        }
        parts.Add(content.Substring(last));
        return parts;
    }

    private static string RangeGroupName(int index) => "range" + index.ToString(CultureInfo.InvariantCulture);

    private sealed record CompiledGlob(Regex? Regex, List<(long Min, long Max)> Ranges);
}
