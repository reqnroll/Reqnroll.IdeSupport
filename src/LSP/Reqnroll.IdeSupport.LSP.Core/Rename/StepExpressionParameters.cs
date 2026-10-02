#nullable enable

using System.Collections.Generic;
using System.Text;

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Shared parsing of step-definition expression text into parameter slots and the static
/// (literal) segments around them. A parameter slot is a Cucumber placeholder (<c>{int}</c>,
/// <c>{string}</c>, <c>{}</c>, …) or a regex capturing group (an unescaped <c>(</c> that is not
/// a non-capturing / look-around group).
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
    /// <paramref name="s"/>, or 0 when no slot starts there.
    /// </summary>
    public static int SlotLengthAt(string s, int index)
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
            return depth == 0 ? j - index : 0;
        }

        return 0;
    }

    /// <summary>
    /// Walks the parameter slots of <paramref name="expression"/> in order, replacing each with
    /// the corresponding entry from <paramref name="values"/> (by position) and leaving the
    /// static text around them unchanged. When there are more slots than values, the extra slots
    /// are dropped (their surrounding static text is kept, but nothing is substituted in their
    /// place) rather than the call failing — the caller decides whether that's acceptable.
    /// </summary>
    internal static string ReplaceSlotsWithValues(string expression, IReadOnlyList<string> values)
    {
        var sb = new StringBuilder();
        var valueIdx = 0;
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i);
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
        var slots = new List<string>();
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i);
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
    /// Splits <paramref name="expression"/> into its static (non-parameter) segments. An
    /// expression with N parameter slots yields N+1 segments (some possibly empty), in order.
    /// </summary>
    public static List<string> StaticSegments(string expression)
    {
        var segments = new List<string>();
        var sb = new StringBuilder();
        var i = 0;
        while (i < expression.Length)
        {
            var slotLength = SlotLengthAt(expression, i);
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
