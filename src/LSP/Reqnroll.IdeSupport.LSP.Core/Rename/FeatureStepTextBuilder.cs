#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Builds feature-step replacement text that preserves the original parameter content
/// when a binding expression is renamed.
/// <para>
/// Two strategies are applied, in order:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Regex injection.</b> Match the old binding regex against the original step text to
///     extract captured parameter values, then inject them into the parameter slots of the new
///     expression. This handles concrete values (e.g. <c>50</c>).
///   </description></item>
///   <item><description>
///     <b>Static-segment substitution.</b> When the regex does not match — e.g. the step uses a
///     Scenario Outline placeholder such as <c>&lt;secondNumber&gt;</c> that is not a numeric value —
///     replace the old expression's literal segments with the new expression's literal segments,
///     leaving whatever sits in the parameter position untouched. This keeps placeholders and other
///     non-matching parameter content intact instead of leaking the binding's <c>{int}</c> token
///     into the feature file.
///   </description></item>
/// </list>
/// <para>If neither strategy applies, returns the new expression unchanged.</para>
/// </summary>
public static class FeatureStepTextBuilder
{
    /// <summary>Rebuilds the feature-file step text for a renamed binding expression, preferring regex group substitution and falling back to static-segment substitution or the unchanged new expression.</summary>
    public static string Build(
        string  newExpression,
        string? oldExpression,
        Regex?  regex,
        string? stepText)
    {
        if (string.IsNullOrEmpty(stepText))
            return newExpression;

        // Static-segment substitution is tried first: it preserves whatever sits in the
        // parameter position (a concrete value, a quoted {string}, or a Scenario Outline
        // placeholder like <secondNumber>) by only rewriting the literal segments. The regex
        // strategy is the fallback for expressions whose static text is itself regex syntax
        // (e.g. non-capturing groups) that does not appear verbatim in the step.
        //
        // Scenario-outline placeholder substitution is the last resort: when both strategies
        // above fail because the step text's literal content was edited independently of the
        // binding expression, but the step still contains Scenario Outline <...> placeholders.
        return TryBuildViaStaticSegments(newExpression, oldExpression, stepText!)
               ?? TryBuildViaRegex(newExpression, regex, stepText!)
               ?? TryBuildViaOutlinePlaceholders(newExpression, stepText!)
               ?? newExpression;
    }

    /// <summary>
    /// Extracts captured parameter values by matching <paramref name="regex"/> against
    /// <paramref name="stepText"/> and injects them into the parameter slots of
    /// <paramref name="newExpression"/>. Returns <c>null</c> when the regex is absent, does not
    /// match, or yields no captures, so the caller can try another strategy.
    /// </summary>
    private static string? TryBuildViaRegex(string newExpression, Regex? regex, string stepText)
    {
        if (regex == null)
            return null;

        var match = regex.Match(stepText);
        if (!match.Success || match.Groups.Count <= 1)
            return null;

        var capturedValues = new List<string>();
        for (int i = 1; i < match.Groups.Count; i++)
            if (match.Groups[i].Success)
                capturedValues.Add(match.Groups[i].Value);

        if (capturedValues.Count == 0)
            return null;

        // Replace parameter slots in the new expression with captured values, in order.
        var result = new StringBuilder();
        int groupIdx = 0;
        int lastEnd = 0;

        for (int i = 0; i < newExpression.Length; i++)
        {
            // Detect start of a capturing group: unescaped '(' not followed by '?:' etc.
            if (newExpression[i] == '(' && !IsEscaped(newExpression, i))
            {
                if (i + 1 < newExpression.Length && newExpression[i + 1] == '?' && i + 2 < newExpression.Length)
                {
                    var lookahead = newExpression.Substring(i + 2, 1);
                    if (lookahead is ":" or "=" or "!" or "<")
                        continue; // non-capturing group, skip
                }

                int depth = 1;
                int j = i + 1;
                while (j < newExpression.Length && depth > 0)
                {
                    if (newExpression[j] == '(' && !IsEscaped(newExpression, j)) depth++;
                    else if (newExpression[j] == ')' && !IsEscaped(newExpression, j)) depth--;
                    j++;
                }

                result.Append(newExpression, lastEnd, i - lastEnd);
                if (groupIdx < capturedValues.Count)
                    result.Append(capturedValues[groupIdx]);
                groupIdx++;
                lastEnd = j;
                i = j - 1;
            }
            else if (newExpression[i] == '{')
            {
                int j = i + 1;
                while (j < newExpression.Length && newExpression[j] != '}') j++;
                if (j < newExpression.Length)
                {
                    result.Append(newExpression, lastEnd, i - lastEnd);
                    if (groupIdx < capturedValues.Count)
                        result.Append(capturedValues[groupIdx]);
                    groupIdx++;
                    lastEnd = j + 1;
                    i = j;
                }
            }
        }

        if (lastEnd < newExpression.Length)
            result.Append(newExpression, lastEnd, newExpression.Length - lastEnd);

        return result.Length > 0 ? result.ToString() : null;
    }

    /// <summary>
    /// Returns whether the character at <paramref name="index"/> is escaped — preceded by an odd
    /// number of consecutive backslashes. A single preceding backslash escapes the character; two
    /// preceding backslashes form an escaped backslash followed by an unescaped character; and so
    /// on by parity. Checking only the single immediately-preceding character (as a naive scan
    /// would) misclassifies a real, unescaped <c>(</c>/<c>)</c> after an escaped backslash (e.g.
    /// <c>\\(foo)</c> — an escaped backslash followed by a genuine capturing group).
    /// </summary>
    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 != 0;
    }

    /// <summary>
    /// Renames the static text of <paramref name="stepText"/> by replacing the literal segments of
    /// <paramref name="oldExpression"/> with those of <paramref name="newExpression"/>, preserving
    /// the parameter regions (the text the step actually carries) verbatim. Returns <c>null</c> when
    /// the expressions cannot be aligned with the step text.
    /// </summary>
    private static string? TryBuildViaStaticSegments(string newExpression, string? oldExpression, string stepText)
    {
        if (string.IsNullOrEmpty(oldExpression))
            return null;

        var oldSegments = StepExpressionParameters.StaticSegments(oldExpression!);
        var newSegments = StepExpressionParameters.StaticSegments(newExpression);

        // Same parameter count (⇒ same number of static segments) is required to map positions.
        if (oldSegments.Count != newSegments.Count)
            return null;

        // No parameters: the step text is fully static; the renamed text is the new expression.
        if (oldSegments.Count == 1)
            return newExpression;

        var prefix = oldSegments[0];
        var suffix = oldSegments[oldSegments.Count - 1];

        if (!stepText.StartsWith(prefix, StringComparison.Ordinal) ||
            !stepText.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var regionStart = prefix.Length;
        var regionEnd   = stepText.Length - suffix.Length;
        if (regionEnd < regionStart)
            return null;

        // Extract each parameter value by locating the interior static segments in order.
        var values = new List<string>();
        var cursor = regionStart;
        for (int i = 1; i < oldSegments.Count - 1; i++)
        {
            var seg = oldSegments[i];
            int idx = seg.Length == 0
                ? cursor
                : stepText.IndexOf(seg, cursor, regionEnd - cursor, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            values.Add(stepText.Substring(cursor, idx - cursor));
            cursor = idx + seg.Length;
        }
        values.Add(stepText.Substring(cursor, regionEnd - cursor));

        var sb = new StringBuilder();
        sb.Append(newSegments[0]);
        for (int i = 0; i < values.Count; i++)
        {
            sb.Append(values[i]);
            sb.Append(newSegments[i + 1]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Detects Scenario Outline placeholders (⟨...⟩ tokens) in <paramref name="stepText"/>
    /// and maps them positionally onto the parameter slots of <paramref name="newExpression"/>.
    /// This handles cases where both regex matching and static-segment alignment fail because
    /// the step text's literal content has been edited independently of the binding expression.
    /// </summary>
    private static string? TryBuildViaOutlinePlaceholders(string newExpression, string stepText)
    {
        var stepPlaceholders = ExtractOutlinePlaceholders(stepText);
        if (stepPlaceholders.Count == 0)
            return null;

        var exprSlots = StepExpressionParameters.ExtractSlots(newExpression);
        if (exprSlots.Count != stepPlaceholders.Count)
            return null;

        // Replace each parameter slot in the new expression with the corresponding
        // Scenario Outline placeholder, preserving the non-parameter text.
        var sb = new StringBuilder();
        int slotIdx = 0;
        for (int i = 0; i < newExpression.Length; i++)
        {
            var slotLength = StepExpressionParameters.SlotLengthAt(newExpression, i);
            if (slotLength > 0)
            {
                sb.Append(stepPlaceholders[slotIdx]);
                slotIdx++;
                i += slotLength - 1;
            }
            else
            {
                sb.Append(newExpression[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Derives a new abstract expression from an in-place feature-file edit. <paramref
    /// name="oldStepText"/> and <paramref name="newStepText"/> are the step's concrete text
    /// (no keyword) before and after the user's edit in the rename dialog; <paramref
    /// name="oldExpression"/> is the binding's current abstract expression. The parameter
    /// values are located in <paramref name="oldStepText"/> using <paramref
    /// name="oldExpression"/>'s static segments, then re-located verbatim in <paramref
    /// name="newStepText"/> so the original parameter slots (<c>{int}</c>, a regex group, …)
    /// can be preserved around whatever static wording the user typed. Returns <see
    /// langword="null"/> when a parameter value can no longer be found verbatim in the edited
    /// text — i.e. the user changed a parameter value rather than the step's wording, which
    /// this flow does not support.
    /// </summary>
    public static string? DeriveExpressionFromEditedText(string oldExpression, string oldStepText, string newStepText)
    {
        var oldSegments = StepExpressionParameters.StaticSegments(oldExpression);

        // No parameters: the whole step text is static, so it becomes the new expression as-is.
        if (oldSegments.Count == 1)
            return newStepText;

        var prefix = oldSegments[0];
        var suffix = oldSegments[oldSegments.Count - 1];
        if (!oldStepText.StartsWith(prefix, StringComparison.Ordinal) ||
            !oldStepText.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var regionStart = prefix.Length;
        var regionEnd = oldStepText.Length - suffix.Length;
        if (regionEnd < regionStart)
            return null;

        // Extract each parameter value from the original concrete text, in order.
        var values = new List<string>();
        var cursor = regionStart;
        for (int i = 1; i < oldSegments.Count - 1; i++)
        {
            var seg = oldSegments[i];
            int idx = seg.Length == 0
                ? cursor
                : oldStepText.IndexOf(seg, cursor, regionEnd - cursor, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            values.Add(oldStepText.Substring(cursor, idx - cursor));
            cursor = idx + seg.Length;
        }
        values.Add(oldStepText.Substring(cursor, regionEnd - cursor));

        var slots = StepExpressionParameters.ExtractSlots(oldExpression);
        if (slots.Count != values.Count)
            return null;

        // Re-locate the same values, in order, within the edited text to recover the new
        // static segments around them.
        var searchFrom = 0;
        var newSegments = new List<string>();
        foreach (var value in values)
        {
            int idx = value.Length == 0
                ? searchFrom
                : newStepText.IndexOf(value, searchFrom, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            newSegments.Add(newStepText.Substring(searchFrom, idx - searchFrom));
            searchFrom = idx + value.Length;
        }
        newSegments.Add(newStepText.Substring(searchFrom));

        var result = new StringBuilder();
        for (int i = 0; i < newSegments.Count; i++)
        {
            result.Append(newSegments[i]);
            if (i < slots.Count)
                result.Append(slots[i]);
        }
        return result.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern
        = new(@"\<([^>]+)\>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Extracts Scenario Outline placeholder values (e.g. {"&lt;result&gt;", "&lt;secondNumber&gt;"}) from <paramref name="stepText"/> in order.</summary>
    private static List<string> ExtractOutlinePlaceholders(string stepText)
    {
        var result = new List<string>();
        var matches = PlaceholderPattern.Matches(stepText);
        foreach (System.Text.RegularExpressions.Match m in matches)
            result.Add(m.Value);
        return result;
    }
}
