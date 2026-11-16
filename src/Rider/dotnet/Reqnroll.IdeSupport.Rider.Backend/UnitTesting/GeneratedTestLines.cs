using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.Rider.Backend.UnitTesting;

/// <summary>
/// Maps the tests Rider discovers in a generated <c>.feature.cs</c> back to lines of the <c>.feature</c> file.
/// <para>
/// Free of the Rider SDK so it can be unit-tested. Keyword-free, so it needs no Gherkin dialect: scenario lines come
/// from the generator's <c>#line</c> directives, and outline example rows from the table rows that follow the
/// outline's last step.
/// </para>
/// </summary>
public static class GeneratedTestLines
{
    private static readonly Regex LineDirective = new Regex(@"^[ \t]*#line[ \t]+(\d+)", RegexOptions.Multiline);

    /// <summary>Attribute names that carry one outline example row in the frameworks Rider runs (MSTest, NUnit, xUnit).</summary>
    private static readonly Regex RowAttribute = new Regex(
        @"\b(?:DataRow|TestCase|InlineData)(?:Attribute)?[ \t]*\(", RegexOptions.CultureInvariant);

    /// <summary>
    /// The zero-based line of the <c>Feature</c> keyword: the first line that is not blank, a comment, a tag or a
    /// language header. -1 if there is none.
    /// </summary>
    /// <param name="getLine">Returns the text of a zero-based line, without its line break.</param>
    public static int FeatureLine(Func<int, string> getLine, int lineCount)
    {
        for (var line = 0; line < lineCount; line++)
        {
            var text = Trim(getLine(line));
            if (text.Length == 0 || text[0] == '#' || text[0] == '@')
                continue;
            return line;
        }

        return -1;
    }

    /// <summary>
    /// The zero-based <c>.feature</c> lines named by the <c>#line N</c> directives in generated code, in order
    /// (<c>#line hidden</c> and <c>#line default</c> are skipped).
    /// </summary>
    public static IReadOnlyList<int> LineDirectives(string generatedCode)
    {
        var lines = new List<int>();
        if (string.IsNullOrEmpty(generatedCode))
            return lines;

        foreach (Match match in LineDirective.Matches(generatedCode))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var oneBased)
                && oneBased > 0)
            {
                lines.Add(oneBased - 1);
            }
        }

        return lines;
    }

    /// <summary>
    /// The zero-based line of the scenario a generated test method was made from: its first <c>#line</c> directive
    /// (the generator emits it for the scenario initialisation, before the steps). -1 if there is none.
    /// </summary>
    public static int ScenarioLine(string methodDeclaration)
    {
        var lines = LineDirectives(methodDeclaration);
        return lines.Count > 0 ? lines[0] : -1;
    }

    /// <summary>
    /// The zero-based line of the last step of a generated test method, or <paramref name="scenarioLine"/> if it has
    /// no steps. Steps are the <c>#line</c> directives after the first.
    /// </summary>
    public static int LastStepLine(string methodDeclaration, int scenarioLine)
    {
        var last = scenarioLine;
        var lines = LineDirectives(methodDeclaration);
        for (var i = 1; i < lines.Count; i++)
            last = Math.Max(last, lines[i]);
        return last;
    }

    /// <summary>
    /// The zero-based lines of a Scenario Outline's example rows (the body rows of its <c>Examples</c> tables, in
    /// document order, which is the order the generator numbers them in). At most <paramref name="maxRows"/> are
    /// returned.
    /// <para>
    /// Scanning starts after the outline's last step and skips that step's own data table or doc string. After
    /// that, each table is an <c>Examples</c> table: its first row is the header and the rest are example rows.
    /// Stopping at <paramref name="maxRows"/> keeps the scan from running into tables of a later
    /// <c>Background</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> ExampleRowLines(Func<int, string> getLine, int lineCount, int lastStepLine, int maxRows)
    {
        var rows = new List<int>();
        if (maxRows <= 0 || lastStepLine < 0)
            return rows;

        var line = SkipStepArgument(getLine, lineCount, lastStepLine + 1);
        var inTable = false;
        for (; line < lineCount && rows.Count < maxRows; line++)
        {
            var text = Trim(getLine(line));
            if (text.Length == 0 || text[0] == '#')
                continue; // Blank lines and comments do not end a table.

            if (text[0] != '|')
            {
                inTable = false;
                continue;
            }

            if (inTable)
                rows.Add(line);
            else
                inTable = true; // The header row.
        }

        return rows;
    }

    /// <summary>
    /// The example-row number (the generator's "pickle index") passed by a generated row attribute, such as
    /// <c>[DataRow("1", "Guitar pick", "0", null)]</c>. Reqnroll passes it as the second-to-last positional
    /// argument, before the example tags, in every framework. Null if the text is not such an attribute.
    /// </summary>
    public static int? PickleIndexOfRowAttribute(string attributeText)
    {
        if (string.IsNullOrEmpty(attributeText))
            return null;

        var match = RowAttribute.Match(attributeText);
        if (!match.Success)
            return null;

        var arguments = PositionalArguments(attributeText, match.Index + match.Length);
        return arguments.Count >= 2 ? DigitStringLiteral(arguments[arguments.Count - 2]) : null;
    }

    /// <summary>The pickle indexes of every row attribute in a generated test method, in declaration order.</summary>
    public static IReadOnlyList<int> PickleIndexesOfRowAttributes(string methodDeclaration)
    {
        var indexes = new List<int>();
        if (string.IsNullOrEmpty(methodDeclaration))
            return indexes;

        foreach (Match match in RowAttribute.Matches(methodDeclaration))
        {
            var arguments = PositionalArguments(methodDeclaration, match.Index + match.Length);
            var index = arguments.Count >= 2 ? DigitStringLiteral(arguments[arguments.Count - 2]) : null;
            if (index != null)
                indexes.Add(index.Value);
        }

        return indexes;
    }

    /// <summary>
    /// The pickle index at the end of an MSTest row's display name, which Reqnroll generates as
    /// <c>Title(value, value, ..., pickleIndex)</c>. Null if the name does not end that way.
    /// </summary>
    public static int? PickleIndexOfDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return null;

        var name = displayName.TrimEnd();
        if (name.Length < 3 || name[name.Length - 1] != ')')
            return null;

        var separator = name.LastIndexOf(',');
        var open = name.LastIndexOf('(');
        var start = Math.Max(separator, open) + 1;
        if (start <= 0)
            return null;

        var digits = name.Substring(start, name.Length - 1 - start).Trim();
        return ParseDigits(digits);
    }

    /// <summary>
    /// The zero-based <c>.feature</c> line of one example row of an outline, given its pickle index and the pickle
    /// indexes of all the outline's rows. The generator numbers rows consecutively in document order, so the row's
    /// position is its index minus the outline's lowest. -1 if it falls outside <paramref name="rowLines"/>.
    /// </summary>
    public static int RowLine(int pickleIndex, IReadOnlyList<int> outlinePickleIndexes, IReadOnlyList<int> rowLines)
    {
        if (outlinePickleIndexes == null || outlinePickleIndexes.Count == 0 || rowLines == null)
            return -1;

        var lowest = int.MaxValue;
        foreach (var index in outlinePickleIndexes)
            lowest = Math.Min(lowest, index);

        var position = pickleIndex - lowest;
        return position >= 0 && position < rowLines.Count ? rowLines[position] : -1;
    }

    private static int SkipStepArgument(Func<int, string> getLine, int lineCount, int line)
    {
        while (line < lineCount && IsBlankOrComment(Trim(getLine(line))))
            line++;
        if (line >= lineCount)
            return line;

        var text = Trim(getLine(line));
        if (text.Length > 0 && text[0] == '|')
        {
            while (line < lineCount)
            {
                text = Trim(getLine(line));
                if (text.Length > 0 && text[0] != '|' && text[0] != '#')
                    break;
                if (text.Length == 0)
                    break;
                line++;
            }

            return line;
        }

        var delimiter = DocStringDelimiter(text);
        if (delimiter == null)
            return line;

        for (line++; line < lineCount; line++)
        {
            if (Trim(getLine(line)).StartsWith(delimiter, StringComparison.Ordinal))
                return line + 1;
        }

        return line;
    }

    /// <summary>
    /// Splits the argument list that starts at <paramref name="start"/> (just after its opening parenthesis) into
    /// top-level positional arguments. Named arguments (<c>Name = value</c> or <c>Name: value</c>) are dropped.
    /// </summary>
    private static List<string> PositionalArguments(string text, int start)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' || (c == '@' && i + 1 < text.Length && text[i + 1] == '"'))
            {
                var end = EndOfStringLiteral(text, i);
                current.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                if (depth == 0)
                {
                    AddPositional(arguments, current);
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddPositional(arguments, current);
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        return new List<string>(); // Unterminated: not an attribute we understand.
    }

    private static void AddPositional(List<string> arguments, StringBuilder argument)
    {
        var text = argument.ToString().Trim();
        if (text.Length == 0)
            return;
        if (Regex.IsMatch(text, @"^@?[A-Za-z_][A-Za-z0-9_]*\s*(=(?!=)|:)"))
            return;
        arguments.Add(text);
    }

    /// <summary>The index just after the string literal starting at <paramref name="start"/>.</summary>
    private static int EndOfStringLiteral(string text, int start)
    {
        if (text[start] == '@')
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                if (text[i] != '"')
                    continue;
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i + 1;
            }

            return text.Length;
        }

        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '"')
                return i + 1;
        }

        return text.Length;
    }

    private static int? DigitStringLiteral(string argument)
    {
        if (argument.Length < 3 || argument[0] != '"' || argument[argument.Length - 1] != '"')
            return null;
        return ParseDigits(argument.Substring(1, argument.Length - 2));
    }

    private static int? ParseDigits(string text)
    {
        if (text.Length == 0)
            return null;
        foreach (var c in text)
        {
            if (c < '0' || c > '9')
                return null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;
    }

    private static bool IsBlankOrComment(string trimmed) => trimmed.Length == 0 || trimmed[0] == '#';

    private static string DocStringDelimiter(string trimmed)
    {
        if (trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
            return "\"\"\"";
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
            return "```";
        return null;
    }

    private static string Trim(string line) => (line ?? string.Empty).Trim().TrimStart('﻿');
}
