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
/// outline's last step, matched to the test's data rows by position.
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
    /// The row attributes (<c>DataRow</c>, <c>TestCase</c>, <c>InlineData</c>) of a generated test method, in
    /// declaration order. The generator writes one per example row in document order, so the n-th attribute is the
    /// n-th row of <see cref="ExampleRowLines"/>. Nothing is read from their positional arguments, whose layout belongs
    /// to the generator (it has carried an internal row index there, which it may drop).
    /// </summary>
    public static IReadOnlyList<GeneratedRow> RowAttributes(string methodDeclaration)
    {
        var rows = new List<GeneratedRow>();
        if (string.IsNullOrEmpty(methodDeclaration))
            return rows;

        foreach (Match match in RowAttribute.Matches(methodDeclaration))
        {
            var arguments = Arguments(methodDeclaration, match.Index + match.Length, out var end);
            if (arguments == null)
                continue; // Unterminated: not an attribute we understand.

            rows.Add(new GeneratedRow(match.Index, end, NamedString(arguments, "DisplayName")));
        }

        return rows;
    }

    /// <summary>
    /// The position of one data row among <paramref name="rows"/> (<see cref="RowAttributes"/> of its method), or -1
    /// if it cannot be told:
    /// <list type="bullet">
    ///   <item>by its own location, when that is one row attribute (NUnit, xUnit); exact even for rows with identical
    ///   values;</item>
    ///   <item>otherwise by its display name, against the attributes' <c>DisplayName</c> (MSTest, whose rows have no
    ///   location of their own).</item>
    /// </list>
    /// </summary>
    /// <param name="rows">The row attributes of the row's method.</param>
    /// <param name="rowOffset">Where the row's own location starts within the method declaration, or null.</param>
    /// <param name="rowText">The text of the row's own location, or null.</param>
    /// <param name="displayName">The row's display name.</param>
    public static int RowPosition(IReadOnlyList<GeneratedRow> rows, int? rowOffset, string rowText, string displayName)
    {
        if (rows == null || rows.Count == 0)
            return -1;

        // A location holding exactly one row attribute is that row's; one holding several is the whole method.
        if (rowOffset != null && !string.IsNullOrEmpty(rowText) && RowAttribute.Matches(rowText).Count == 1)
        {
            var offset = rowOffset.Value + RowAttribute.Match(rowText).Index;
            for (var i = 0; i < rows.Count; i++)
            {
                if (offset >= rows[i].Start && offset < rows[i].End)
                    return i;
            }
        }

        if (!string.IsNullOrEmpty(displayName))
        {
            var name = displayName.Trim();
            for (var i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].DisplayName, name, StringComparison.Ordinal))
                    return i;
            }
        }

        return -1;
    }

    /// <summary>The <c>.feature</c> line of the example row at <paramref name="position"/>; -1 if there is none.</summary>
    public static int RowLine(int position, IReadOnlyList<int> rowLines) =>
        rowLines != null && position >= 0 && position < rowLines.Count ? rowLines[position] : -1;

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
    /// Splits the argument list that starts at <paramref name="start"/> (just after its opening parenthesis) into its
    /// top-level arguments, positional and named, and gives the index just after its closing parenthesis. Null if the
    /// list is not closed.
    /// </summary>
    private static List<string> Arguments(string text, int start, out int end)
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
                var literalEnd = EndOfStringLiteral(text, i);
                current.Append(text, i, literalEnd - i);
                i = literalEnd;
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
                    AddArgument(arguments, current);
                    end = i + 1;
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddArgument(arguments, current);
                current.Clear();
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        end = text.Length;
        return null;
    }

    private static void AddArgument(List<string> arguments, StringBuilder argument)
    {
        var text = argument.ToString().Trim();
        if (text.Length > 0)
            arguments.Add(text);
    }

    /// <summary>The value of the named string argument <c>Name = "..."</c> (or <c>Name: "..."</c>); null if absent.</summary>
    private static string NamedString(IEnumerable<string> arguments, string name)
    {
        var named = new Regex(@"^" + Regex.Escape(name) + @"\s*(?:=(?!=)|:)\s*(?<literal>@?"".*)$", RegexOptions.Singleline);
        foreach (var argument in arguments)
        {
            var match = named.Match(argument);
            if (match.Success)
                return StringLiteralValue(match.Groups["literal"].Value.Trim());
        }

        return null;
    }

    /// <summary>The value of a C# string literal, regular or verbatim; null if the text is not one.</summary>
    private static string StringLiteralValue(string literal)
    {
        if (literal.Length >= 3 && literal[0] == '@' && literal[1] == '"' && literal[literal.Length - 1] == '"')
            return literal.Substring(2, literal.Length - 3).Replace("\"\"", "\"");
        if (literal.Length < 2 || literal[0] != '"' || literal[literal.Length - 1] != '"')
            return null;

        var value = new StringBuilder();
        for (var i = 1; i < literal.Length - 1; i++)
        {
            var c = literal[i];
            if (c != '\\' || i + 1 >= literal.Length - 1)
            {
                value.Append(c);
                continue;
            }

            var escaped = literal[++i];
            value.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                _ => escaped,
            });
        }

        return value.ToString();
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

/// <summary>One row attribute of a generated test method (<see cref="GeneratedTestLines.RowAttributes"/>).</summary>
public sealed class GeneratedRow
{
    public GeneratedRow(int start, int end, string displayName)
    {
        Start = start;
        End = end;
        DisplayName = displayName;
    }

    /// <summary>Where the attribute's name starts in the method declaration.</summary>
    public int Start { get; }

    /// <summary>Just after the attribute's closing parenthesis.</summary>
    public int End { get; }

    /// <summary>The attribute's <c>DisplayName</c> argument (MSTest), or null.</summary>
    public string DisplayName { get; }
}
