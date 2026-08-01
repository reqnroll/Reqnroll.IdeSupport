#nullable enable

using System.IO;
using System.Text;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Assembles a complete C# step-definition source file from one or more rendered step snippets.
/// </summary>
public static class StepDefinitionFileBuilder
{
    /// <summary>
    /// Builds the full content of a new <c>.cs</c> step-definition file.
    /// </summary>
    /// <param name="snippets">
    /// Pre-rendered method snippets (each already indented at one level).
    /// Produced by <see cref="StepSkeletonRenderer.Render"/>.
    /// </param>
    /// <param name="className">The step-definition class name (e.g. <c>AdditionStepDefinitions</c>).</param>
    /// <param name="namespace">The target namespace.</param>
    /// <param name="csharpConfig">Controls block-scoped vs. file-scoped namespace style.</param>
    /// <param name="indent">The indentation unit (e.g. four spaces).</param>
    /// <param name="newLine">The line-ending string.</param>
    public static string BuildNewFile(
        IReadOnlyList<string>             snippets,
        string                            className,
        string                            @namespace,
        CSharpCodeGenerationConfiguration csharpConfig,
        string                            indent,
        string                            newLine)
    {
        bool fileScoped = csharpConfig.UseFileScopedNamespaces;

        var sb = new StringBuilder();

        // Using directives
        sb.Append("using System;").Append(newLine);
        sb.Append("using Reqnroll;").Append(newLine);
        sb.Append(newLine);

        if (fileScoped)
        {
            // file-scoped namespace: no braces, class at top level
            sb.Append($"namespace {@namespace};").Append(newLine);
            sb.Append(newLine);
            sb.Append("[Binding]").Append(newLine);
            sb.Append($"public class {className}").Append(newLine);
            sb.Append('{').Append(newLine);
            // Snippets are already pre-indented at one level; no extra prefix needed.
            AppendSnippets(sb, snippets, newLine, classIndent: "");
            sb.Append('}').Append(newLine);
        }
        else
        {
            // block-scoped namespace
            sb.Append($"namespace {@namespace}").Append(newLine);
            sb.Append('{').Append(newLine);
            sb.Append(indent).Append("[Binding]").Append(newLine);
            sb.Append(indent).Append($"public class {className}").Append(newLine);
            sb.Append(indent).Append('{').Append(newLine);
            // Snippets are pre-indented at one level; add one more for the class body.
            AppendSnippets(sb, snippets, newLine, classIndent: indent);
            sb.Append(indent).Append('}').Append(newLine);
            sb.Append('}').Append(newLine);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Inserts <paramref name="snippets"/> as new methods into the body of the first class found
    /// in <paramref name="existingContent"/>, immediately before its closing brace.
    /// </summary>
    /// <remarks>
    /// Uses a lightweight heuristic scan (not a full C# parser): string/char literals and comments
    /// are masked out before locating the class's braces, so brace characters inside them don't
    /// confuse the scan. Returns <see langword="null"/> when the scan can't confidently locate a
    /// class body (no <c>class</c> keyword, unterminated literal/comment, or unbalanced braces) —
    /// callers should fall back to creating a new file rather than risk corrupting a hand-written
    /// one. Member indentation is detected from the first existing member line inside the class
    /// body; <paramref name="indent"/> is used only as a fallback for an empty class body.
    /// </remarks>
    public static string? AppendToFile(
        string                 existingContent,
        IReadOnlyList<string>  snippets,
        string                 indent,
        string                 newLine)
    {
        if (snippets.Count == 0) return existingContent;

        var masked = MaskLiteralsAndComments(existingContent);
        if (masked is null) return null;

        var classMatch = System.Text.RegularExpressions.Regex.Match(masked, @"\bclass\b");
        if (!classMatch.Success) return null;

        var openBraceIndex = masked.IndexOf('{', classMatch.Index);
        if (openBraceIndex < 0) return null;

        var closeBraceIndex = FindMatchingCloseBrace(masked, openBraceIndex);
        if (closeBraceIndex < 0) return null;

        var classIndent = DetectMemberIndent(existingContent, openBraceIndex, closeBraceIndex, indent);

        // Trim trailing whitespace back from the closing brace so repeated appends don't
        // accumulate blank lines between the last member and the brace.
        var insertAt = closeBraceIndex;
        while (insertAt > openBraceIndex + 1 && char.IsWhiteSpace(existingContent[insertAt - 1]))
            insertAt--;
        bool bodyHasMembers = insertAt > openBraceIndex + 1;

        var sb = new StringBuilder(existingContent.Length + 256);
        sb.Append(existingContent, 0, insertAt);
        sb.Append(newLine);
        if (bodyHasMembers) sb.Append(newLine);
        // Snippets already carry one baked-in `indent` unit per line (the class-member level —
        // see BuildNewFile's file-scoped branch). AppendSnippets treats its classIndent as an
        // *additional* prefix on top of that, which is right for BuildNewFile (0 or 1 extra level
        // depending on namespace style) but wrong here: classIndent is the file's *actual* member
        // indent, not an extra level, so appending it as a prefix on top of the snippet's own
        // baked-in indent doubles it. Re-indent instead: strip the baked-in unit, then apply the
        // detected indent as the sole prefix.
        AppendReindentedSnippets(sb, snippets, newLine, indent, classIndent);
        sb.Append(newLine);
        sb.Append(existingContent, closeBraceIndex, existingContent.Length - closeBraceIndex);

        return sb.ToString();
    }

    /// <summary>
    /// Returns a same-length copy of <paramref name="content"/> with every string/char literal and
    /// comment replaced by spaces (newlines preserved), so brace-matching on the result ignores
    /// braces that appear inside literals or comments. Returns <see langword="null"/> if a literal
    /// or comment is left unterminated (malformed/unparseable source — bail rather than guess).
    /// </summary>
    private static string? MaskLiteralsAndComments(string content)
    {
        var mask = content.ToCharArray();
        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                int start = i;
                while (i < content.Length && content[i] != '\n') i++;
                Clear(mask, start, i);
                continue;
            }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                int start = i;
                int end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                Clear(mask, start, i);
                continue;
            }

            if (c == '@' && i + 1 < content.Length && content[i + 1] == '"')
            {
                int start = i;
                i += 2;
                while (true)
                {
                    int q = content.IndexOf('"', i);
                    if (q < 0) return null;
                    if (q + 1 < content.Length && content[q + 1] == '"') { i = q + 2; continue; }
                    i = q + 1;
                    break;
                }
                Clear(mask, start, i);
                continue;
            }

            if (c == '"')
            {
                int start = i;
                i++;
                while (i < content.Length && content[i] != '"')
                {
                    if (content[i] == '\\' && i + 1 < content.Length) i++;
                    i++;
                }
                if (i >= content.Length) return null;
                i++;
                Clear(mask, start, i);
                continue;
            }

            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < content.Length && content[i] != '\'')
                {
                    if (content[i] == '\\' && i + 1 < content.Length) i++;
                    i++;
                }
                if (i >= content.Length) return null;
                i++;
                Clear(mask, start, i);
                continue;
            }

            i++;
        }

        return new string(mask);

        static void Clear(char[] buffer, int start, int end)
        {
            for (int j = start; j < end; j++)
                if (buffer[j] != '\n') buffer[j] = ' ';
        }
    }

    /// <summary>Finds the index of the <c>}</c> that closes the <c>{</c> at <paramref name="openBraceIndex"/> by depth counting.</summary>
    private static int FindMatchingCloseBrace(string maskedContent, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < maskedContent.Length; i++)
        {
            if (maskedContent[i] == '{') depth++;
            else if (maskedContent[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Returns the leading whitespace of the first non-blank line strictly between the two brace indices, or <paramref name="fallback"/> if the body is empty.</summary>
    private static string DetectMemberIndent(string content, int openBraceIndex, int closeBraceIndex, string fallback)
    {
        int i = openBraceIndex + 1;
        while (i < closeBraceIndex)
        {
            int lineEnd = content.IndexOf('\n', i);
            if (lineEnd < 0 || lineEnd > closeBraceIndex) lineEnd = closeBraceIndex;

            var line = content.Substring(i, lineEnd - i);
            var trimmed = line.Trim(' ', '\t', '\r');
            if (trimmed.Length > 0)
                return line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length);

            i = lineEnd + 1;
        }
        return fallback;
    }

    private static void AppendSnippets(
        StringBuilder          sb,
        IReadOnlyList<string>  snippets,
        string                 newLine,
        string                 classIndent)
    {
        for (int i = 0; i < snippets.Count; i++)
        {
            // Normalize line endings so splitting works regardless of snippet origin.
            var normalized = snippets[i].Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');

            // Trim trailing empty strings produced by a trailing newline in the snippet.
            int end = lines.Length;
            while (end > 0 && lines[end - 1].Length == 0)
                end--;

            for (int j = 0; j < end; j++)
            {
                var line = lines[j];
                if (line.Length == 0)
                    sb.Append(newLine);
                else
                    sb.Append(classIndent).Append(line).Append(newLine);
            }

            // Blank line between methods, but not after the last one.
            if (i < snippets.Count - 1)
                sb.Append(newLine);
        }
    }

    /// <summary>
    /// Like <see cref="AppendSnippets"/>, but for the append-to-existing-file path: each snippet
    /// line's baked-in <paramref name="sourceIndentUnit"/> prefix is stripped and replaced with
    /// <paramref name="targetIndent"/> (the indentation actually used by the target file's
    /// existing members) instead of being added on top of it.
    /// </summary>
    private static void AppendReindentedSnippets(
        StringBuilder          sb,
        IReadOnlyList<string>  snippets,
        string                 newLine,
        string                 sourceIndentUnit,
        string                 targetIndent)
    {
        for (int i = 0; i < snippets.Count; i++)
        {
            var normalized = snippets[i].Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');

            int end = lines.Length;
            while (end > 0 && lines[end - 1].Length == 0)
                end--;

            for (int j = 0; j < end; j++)
            {
                var line = lines[j];
                if (line.Length == 0)
                {
                    sb.Append(newLine);
                    continue;
                }

                var content = line.StartsWith(sourceIndentUnit, StringComparison.Ordinal)
                    ? line.Substring(sourceIndentUnit.Length)
                    : line.TrimStart(' ', '\t');

                sb.Append(targetIndent).Append(content).Append(newLine);
            }

            if (i < snippets.Count - 1)
                sb.Append(newLine);
        }
    }

    // ── File naming helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Derives a step-definition class name from a feature file path.
    /// E.g. <c>addition.feature</c> → <c>AdditionStepDefinitions</c>.
    /// </summary>
    public static string ClassNameFromFeaturePath(string featureFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(featureFilePath);
        return ToPascalCase(baseName) + "StepDefinitions";
    }

    /// <summary>
    /// Derives the target <c>.cs</c> file path from the feature file path.
    /// Prefers placing the file in a sibling <c>StepDefinitions/</c> directory if one exists.
    /// </summary>
    public static string TargetFilePath(string featureFilePath, string className)
    {
        var featureDir  = Path.GetDirectoryName(featureFilePath) ?? string.Empty;
        var stepDefsDir = Path.Combine(featureDir, "StepDefinitions");

        var targetDir = Directory.Exists(stepDefsDir) ? stepDefsDir : featureDir;
        return Path.Combine(targetDir, className + ".cs");
    }

    /// <summary>
    /// Derives a namespace from a project root, default namespace, and a target file path.
    /// </summary>
    public static string DeriveNamespace(
        string projectFolder,
        string defaultNamespace,
        string targetFilePath)
    {
        var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetFilePath)) ?? string.Empty;
        var projFull  = Path.GetFullPath(projectFolder);

        if (!PathUtils.IsUnderFolder(targetDir, projFull))
            return defaultNamespace;

        var relative = targetDir.Substring(projFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
            return defaultNamespace;

        var nsSegments = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            .Select(ToPascalCase)
            .Where(s => s.Length > 0);

        return defaultNamespace + "." + string.Join(".", nsSegments);
    }

    private static string ToPascalCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = System.Text.RegularExpressions.Regex.Split(s, @"[^a-zA-Z0-9]+")
                    .Where(p => p.Length > 0)
                    .Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1));
        return string.Concat(parts);
    }
}
