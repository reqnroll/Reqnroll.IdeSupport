#nullable enable

using System.Collections.Generic;
using System.Text;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Shared parsing of step-definition expression text into parameter slots and the static
/// (literal) segments around them. A parameter slot is a Cucumber placeholder (<c>{int}</c>,
/// <c>{string}</c>, <c>{}</c>, …) or a regex capturing group (an unescaped <c>(</c> that is not
/// a non-capturing / look-around group). In a Cucumber expression only the placeholders are
/// slots: <c>(text)</c> is optional text, not a parameter.
/// </summary>
public static class StepExpressionParameters
{
    /// <summary>
    /// Returns whether the character at <paramref name="index"/> in <paramref name="s"/> is
    /// escaped — preceded by an odd number of consecutive backslashes. A single preceding
    /// backslash escapes the character; two preceding backslashes form an escaped backslash
    /// followed by an unescaped character; and so on by parity. Checking only the single
    /// immediately-preceding character (as a naive scan would) misclassifies a real, unescaped
    /// <c>(</c>/<c>)</c> after an escaped backslash (e.g. <c>\\(foo)</c> — an escaped backslash
    /// followed by a genuine capturing group).
    /// </summary>
    internal static bool IsEscaped(string s, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && s[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 != 0;
    }

    /// <summary>
    /// Returns the length of the parameter slot starting at <paramref name="index"/> in
    /// <paramref name="s"/>, or 0 when no slot starts there. <paramref name="isCucumber"/> says
    /// whether <paramref name="s"/> is a Cucumber expression, where <c>(</c> opens optional text
    /// rather than a capturing group.
    /// </summary>
    public static int SlotLengthAt(string s, int index, bool isCucumber = false)
    {
        var c = s[index];

        if (c == '{')
        {
            var j = index + 1;
            while (j < s.Length && s[j] != '}') j++;
            return j < s.Length ? j - index + 1 : 0;
        }

        if (c == '(' && !IsEscaped(s, index))
        {
            // Skip non-capturing / look-around groups: (?:  (?=  (?!  (?<
            if (index + 2 < s.Length && s[index + 1] == '?' &&
                (s[index + 2] == ':' || s[index + 2] == '=' || s[index + 2] == '!' || s[index + 2] == '<'))
                return 0;

            var depth = 1;
            var j = index + 1;
            while (j < s.Length && depth > 0)
            {
                if (s[j] == '(' && !IsEscaped(s, j)) depth++;
                else if (s[j] == ')' && !IsEscaped(s, j)) depth--;
                j++;
            }
            var length = depth == 0 ? j - index : 0;

            // In a Cucumber expression a group of plain words is optional text. A group holding
            // regex syntax is still a capturing group, which keeps a hybrid such as
            // "{int} cukes and (\d+) apples" resolving as it always has.
            if (isCucumber && length > 0 && !ContainsRegexSyntax(s, index + 1, length - 2))
                return 0;

            return length;
        }

        return 0;
    }

    /// <summary>Whether <paramref name="expression"/> is written in Cucumber-expression syntax rather than as a regex.</summary>
    public static bool IsCucumberExpression(string expression)
        => CucumberExpressionDetector.IsCucumberExpression(expression);

    private static bool ContainsRegexSyntax(string s, int start, int count)
        => s.IndexOfAny(RegexMetacharacters, start, count) >= 0;

    private static readonly char[] RegexMetacharacters = { '\\', '.', '*', '+', '?', '[', ']', '|', '^', '$', '(', ')' };

    /// <summary>
    /// Walks the parameter slots of <paramref name="expression"/> in order, replacing each with
    /// the corresponding entry from <paramref name="values"/> (by position) and leaving the
    /// static text around them unchanged. When there are more slots than values, the extra slots
    /// are dropped (their surrounding static text is kept, but nothing is substituted in their
    /// place) rather than the call failing — the caller decides whether that's acceptable.
    /// </summary>
    internal static string ReplaceSlotsWithValues(string expression, IReadOnlyList<string> values)
    {
        var isCucumber = CucumberExpressionDetector.IsCucumberExpression(expression);
        var sb = new StringBuilder();
        var valueIdx = 0;
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i, isCucumber);
            if (slotLength > 0)
            {
                if (valueIdx < values.Count)
                    sb.Append(values[valueIdx]);
                valueIdx++;
                i += slotLength;
            }
            else
            {
                sb.Append(expression[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns the ordered parameter-slot substrings of <paramref name="expression"/>.</summary>
    public static List<string> ExtractSlots(string expression)
    {
        var isCucumber = CucumberExpressionDetector.IsCucumberExpression(expression);
        var slots = new List<string>();
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i, isCucumber);
            if (slotLength > 0)
            {
                slots.Add(expression.Substring(i, slotLength));
                i += slotLength;
            }
            else
            {
                i++;
            }
        }
        return slots;
    }

    /// <summary>
    /// Whether <paramref name="sourceExpression"/> and <paramref name="registryExpression"/> have
    /// the same text around their parameter slots — e.g. <c>the number is {int}</c> and its regex
    /// projection <c>the number is (.*)</c> — ignoring one leading <c>^</c> and trailing <c>$</c>.
    /// </summary>
    public static bool HasSameStaticText(string sourceExpression, string? registryExpression)
    {
        if (registryExpression == null)
            return false;

        return StaticSegments(TrimAnchors(sourceExpression))
            .SequenceEqual(StaticSegments(TrimAnchors(registryExpression)), StringComparer.Ordinal);

        static string TrimAnchors(string s)
        {
            if (s.StartsWith("^", StringComparison.Ordinal))
                s = s.Substring(1);
            if (s.EndsWith("$", StringComparison.Ordinal) && !s.EndsWith(@"\$", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1);
            return s;
        }
    }

    /// <summary>
    /// Splits <paramref name="expression"/> into its static (non-parameter) segments. An
    /// expression with N parameter slots yields N+1 segments (some possibly empty), in order.
    /// </summary>
    public static List<string> StaticSegments(string expression)
    {
        var isCucumber = CucumberExpressionDetector.IsCucumberExpression(expression);
        var segments = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i, isCucumber);
            if (slotLength > 0)
            {
                segments.Add(sb.ToString());
                sb.Clear();
                i += slotLength;
            }
            else
            {
                sb.Append(expression[i]);
                i++;
            }
        }
        segments.Add(sb.ToString());
        return segments;
    }
}
