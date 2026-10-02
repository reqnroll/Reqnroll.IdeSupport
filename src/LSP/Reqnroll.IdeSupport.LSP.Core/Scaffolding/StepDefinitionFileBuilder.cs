#nullable enable

using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    /// The class body is located with Roslyn (issue #586). This previously used a hand-rolled
    /// character scan that masked out literals and comments before brace-matching. That scan was
    /// the single most complex method in the repository (CX=32) and got most cases right, but its
    /// naive quote pairing mis-tracked raw string literals whose body contains a quote run — a
    /// four-quote-delimited literal containing a three-quote sequence, say — so a brace inside
    /// such a literal was treated as real code, the class braces failed to balance, and the
    /// append was abandoned. <c>LSP.Core</c> already references <c>Microsoft.CodeAnalysis.CSharp</c>
    /// for the binding parser, so the parser that answers this question exactly was already loaded.
    /// <para>
    /// Returns <see langword="null"/> when the class body can't be confidently located — no class
    /// declaration, a missing closing brace, or source whose lexical structure is unterminated
    /// (see <see cref="UnterminatedLexicalStructureIds"/>). Callers should fall back to creating a
    /// new file rather than risk corrupting a hand-written one. Ordinary syntax errors elsewhere
    /// in the file (a missing semicolon, say) do <em>not</em> block the append, matching the
    /// previous scan's tolerance — Roslyn's error recovery still locates the braces correctly.
    /// </para>
    /// <para>
    /// Member indentation is detected from the first existing member line inside the class body;
    /// <paramref name="indent"/> is used only as a fallback for an empty class body.
    /// </para>
    /// </remarks>
    public static string? AppendToFile(
        string                 existingContent,
        IReadOnlyList<string>  snippets,
        string                 indent,
        string                 newLine)
    {
        if (snippets.Count == 0) return existingContent;

        if (!TryLocateClassBody(existingContent, out var openBraceIndex, out var closeBraceIndex))
            return null;

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
    /// Syntax-diagnostic IDs meaning "a literal or comment is never terminated". These are the
    /// conditions under which the class body cannot be trusted, because everything after the
    /// unterminated construct is lexed as part of it.
    /// </summary>
    /// <remarks>
    /// Deliberately a narrow list rather than "any error diagnostic": the previous hand-rolled
    /// scan bailed only on an unterminated literal/comment and tolerated every other kind of
    /// malformed source, and widening that here would turn appends into new-file fallbacks for
    /// files that are merely mid-edit. Note that an unterminated <em>string</em> does not
    /// necessarily leave the closing brace missing — Roslyn recovers and still reports a
    /// close-brace token — so the brace check alone is not sufficient and this list is load-bearing.
    /// </remarks>
    private static readonly string[] UnterminatedLexicalStructureIds =
    [
        "CS1010", // Newline in constant (unterminated string or char literal)
        "CS1035", // End-of-file found, '*/' expected (unterminated block comment)
        "CS1039", // Unterminated string literal
        "CS8997", // Unterminated raw string literal
    ];

    /// <summary>
    /// Locates the body braces of the first class declaration in <paramref name="content"/> using
    /// Roslyn. Returns <see langword="false"/> when there is no class declaration, its closing
    /// brace is missing, or the source contains an unterminated literal/comment.
    /// </summary>
    private static bool TryLocateClassBody(string content, out int openBraceIndex, out int closeBraceIndex)
    {
        openBraceIndex  = -1;
        closeBraceIndex = -1;

        var tree = CSharpSyntaxTree.ParseText(content);

        foreach (var diagnostic in tree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error &&
                UnterminatedLexicalStructureIds.Contains(diagnostic.Id))
            {
                return false;
            }
        }

        // Document order, matching the previous scan's "first `class` keyword" behaviour.
        var classDeclaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();

        if (classDeclaration is null) return false;
        if (classDeclaration.OpenBraceToken.IsMissing || classDeclaration.CloseBraceToken.IsMissing)
            return false;

        openBraceIndex  = classDeclaration.OpenBraceToken.SpanStart;
        closeBraceIndex = classDeclaration.CloseBraceToken.SpanStart;
        return true;
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
