using System.Collections.Immutable;
using System.Text;

namespace Reqnroll.IdeSupport.LSP.Core.Completions;

/// <summary>
/// Splits a step-definition regex expression into alternating text and capturing-group parts
/// so that the <see cref="StepDefinitionSampler"/> can substitute type placeholders.
/// </summary>
public sealed class RegexStepDefinitionExpressionAnalyzer
{
    /// <summary>Splits the given step-definition regex expression into alternating text and capturing-group parts.</summary>
    public AnalyzedStepDefinitionExpression Parse(string expression)
    {
        var parts = SplitByGroups(expression);
        return new AnalyzedStepDefinitionExpression(parts);
    }

    private static ImmutableArray<AnalyzedStepDefinitionExpressionPart> SplitByGroups(string regexString)
    {
        var parts     = new List<AnalyzedStepDefinitionExpressionPart>();
        var escaped   = new StringBuilder();
        var unescaped = new StringBuilder();

        const char maskChar      = '\\';
        const char groupOpenChar = '(';
        var maskedRegexChars = new[] { maskChar, '+', '.', '*', '?', '|', '{', '[', groupOpenChar, '^', '$', '#' };

        int  position    = 0;
        bool isSimpleText = true;

        while (position < regexString.Length)
        {
            int index = regexString.IndexOfAny(maskedRegexChars, position);
            if (index < 0)
            {
                var tail = regexString.Substring(position);
                escaped.Append(tail);
                unescaped.Append(tail);
                break;
            }

            char ch = regexString[index];

            if (ch == maskChar && index < regexString.Length - 1)
            {
                // Escaped character — append raw bytes to escaped, just the literal char to unescaped
                escaped.Append(regexString, position, index - position + 2);
                unescaped.Append(regexString, position, index - position);
                unescaped.Append(regexString[index + 1]);

                // Issue #958: character classes such as \d, \w and \s (and their negations, plus
                // the \b word-boundary assertion) are regex operators, not literal characters. Marking
                // the containing text part as non-simple stops the StepDefinitionSampler from emitting
                // the escaped letter literally (e.g. "I have d apples" for @"I have \d apples").
                if (IsEscapedCharacterClass(regexString[index + 1]))
                    isSimpleText = false;

                position = index + 2;
            }
            else if (ch == groupOpenChar && !IsNonCapturingGroup(regexString, index))
            {
                // Start of a capturing group — flush accumulated text then capture the group
                if (index > position)
                {
                    var text = regexString.Substring(position, index - position);
                    escaped.Append(text);
                    unescaped.Append(text);
                }

                parts.Add(CreateTextPart(escaped.ToString(), unescaped.ToString(), isSimpleText));
                escaped.Clear();
                unescaped.Clear();
                isSimpleText = true;

                int closeIndex = FindGroupCloseIndex(regexString, index) + 1;
                parts.Add(new AnalyzedStepDefinitionExpressionParameterPart(
                    regexString.Substring(index, closeIndex - index)));
                position = closeIndex;
            }
            else
            {
                // Regex operator outside a capturing group — text is NOT simple
                escaped.Append(regexString, position, index - position + 1);
                unescaped.Append(regexString, position, index - position);
                position     = index + 1;
                isSimpleText = false;
            }
        }

        parts.Add(CreateTextPart(escaped.ToString(), unescaped.ToString(), isSimpleText));
        return parts.ToImmutableArray();
    }

    private static AnalyzedStepDefinitionExpressionPart CreateTextPart(
        string text, string unescapedText, bool isSimpleText)
        => isSimpleText
            ? new AnalyzedStepDefinitionExpressionSimpleTextPart(text, unescapedText)
            : new AnalyzedStepDefinitionExpressionWithOperatorsTextPart(text);

    private static int FindGroupCloseIndex(string s, int openPos)
    {
        int nesting = 0;
        for (int i = openPos; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '(')  { nesting++; continue; }
            if (s[i] == ')') { if (--nesting == 0) return i; }
        }
        return s.Length - 1;
    }

    private static bool IsNonCapturingGroup(string s, int index)
        => index + 2 < s.Length && s[index + 1] == '?' && s[index + 2] == ':';

    /// <summary>Escaped character classes that are regex operators rather than literal characters (issue #958).</summary>
    private static bool IsEscapedCharacterClass(char c)
        => c is 'd' or 'w' or 's' or 'D' or 'W' or 'S' or 'b';
}
