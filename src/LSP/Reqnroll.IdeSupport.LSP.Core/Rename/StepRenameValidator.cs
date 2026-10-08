#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

namespace Reqnroll.IdeSupport.LSP.Core.Rename;

/// <summary>
/// Applies validation rules for the Step Rename refactoring feature.
/// All methods return <see langword="null"/> on success (no error) or a <see cref="ValidationError"/>
/// describing the failure. The class is stateless — all inputs are passed explicitly.
/// </summary>
public static class StepRenameValidator
{
    /// <summary>
    /// Characters that are meaningful in Cucumber Expression syntax and must not appear in
    /// non-parameter text: parameter delimiters (<c>{ }</c>), optional-text delimiters
    /// (<c>( )</c>), the escape character (<c>\</c>), and the alternative-text separator
    /// (<c>/</c>). Everything else — including <c>$ ^ ? * + [ ] |</c> — is a plain literal
    /// character in a Cucumber Expression (issue #649), unlike in a regex.
    /// </summary>
    private static readonly char[] CucumberExpressionOperators = { '{', '}', '(', ')', '\\', '/' };

    /// <summary>Characters that are regex operators and must not appear in non-parameter text of a regex-authored binding.</summary>
    private static readonly char[] RegexOperators = { '?', '*', '+', '[', ']', '{', '}', '(', ')', '^', '$', '|' };

    // ── Validation methods ─────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the cursor position is on a file type that can be renamed (.feature or .cs).
    /// Returns an error if the URI does not correspond to a supported file type.
    /// </summary>
    public static ValidationError? ValidateCursorPosition(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        return new ValidationError("No step definition found at this position", "position");
    }

    /// <summary>
    /// Validates that the binding expression is a detectable string literal (Rule 2).
    /// Returns an error if the expression is null or empty, meaning the attribute
    /// argument is not a string literal (e.g., a constant reference, concatenation, or nameof).
    /// </summary>
    public static ValidationError? ValidateExpressionIsStringLiteral(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
            return new ValidationError("Step definition expression cannot be detected", "expression");
        return null;
    }

    /// <summary>
    /// Validates the proposed new name against the original expression (Rules 3-6).
    /// Null return = valid.
    /// </summary>
    /// <param name="originalExpression">The binding's current expression string.</param>
    /// <param name="newName">The proposed replacement expression from the rename dialog.</param>
    public static ValidationError? ValidateNewName(string originalExpression, string newName)
    {
        if (string.IsNullOrEmpty(newName))
            return new ValidationError("The new step text cannot be empty", "rename");

        // Rule 3: non-parameter parts must not contain expression operators. Which characters
        // count as "operators" depends on the original binding's own syntax (issue #649): a
        // literal '$' (e.g. a currency amount in "the price is ${float}") is meaningless
        // punctuation in a Cucumber Expression but a real anchor in a regex, so the two syntaxes
        // need different forbidden-character sets rather than one shared regex-operator list.
        // Rule 4: parameter count must match. A "parameter slot" is syntax-aware (issue #960):
        //  - for a regex-authored binding it is a capturing group — StepExpressionParameters'
        //    balanced-group scan already skips non-capturing / look-around groups and honours
        //    escaping — plus any {…} placeholder;
        //  - for a Cucumber-authored binding the only slots are {…} placeholders. A parenthesised
        //    "(text)" there is optional text, not a parameter, so dropping it is not a
        //    parameter-count change.
        var isCucumber = CucumberExpressionDetector.IsCucumberExpression(originalExpression);
        var newScan = ScanExpression(newName, isCucumber);

        // Scan non-parameter segments for operators (Rule 3). A matched group — a regex
        // non-capturing / look-around group, or a Cucumber Expression's optional text — is
        // expression structure rather than literal text, so its own delimiters are never
        // flagged here; only an unmatched/unescaped operator in the surrounding text is.
        var forbiddenOperators = isCucumber ? CucumberExpressionOperators : RegexOperators;
        foreach (var segment in newScan.StaticSegments)
        {
            if (segment.IndexOfAny(forbiddenOperators) >= 0)
                return new ValidationError("The non-parameter parts cannot contain expression operators", "rename");
        }

        if (ScanExpression(originalExpression, isCucumber).ParameterCount != newScan.ParameterCount)
            return new ValidationError("Parameter count mismatch", "rename");

        return null; // all passed
    }

    /// <summary>
    /// Validates that the project is ready for rename operations (Rule 7).
    /// </summary>
    public static ValidationError? ValidateProjectState(bool isInitialized, bool hasFeatureFiles)
    {
        if (!isInitialized)
            return new ValidationError("The project is not initialized yet", "project");
        if (!hasFeatureFiles)
            return new ValidationError("No Reqnroll project with feature files found", "project");
        return null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="expression"/> once, counting its parameter slots and collecting the
    /// static (non-parameter) text around them. Both the slot count and the static segments are
    /// syntax-aware — see <see cref="ValidateNewName"/> (issue #960).
    /// </summary>
    private static (int ParameterCount, List<string> StaticSegments) ScanExpression(
        string expression, bool isCucumber)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var count = 0;
        var i = 0;

        while (i < expression.Length)
        {
            var slotLength = ParameterSlotLengthAt(expression, i, isCucumber);
            if (slotLength > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
                count++;
                i += slotLength;
                continue;
            }

            // A matched group — a regex non-capturing / look-around group, or a Cucumber
            // Expression's optional text — is expression structure rather than literal text:
            // it is neither a slot nor scanned for operators.
            var groupLength = GroupLengthAt(expression, i);
            if (groupLength > 0)
            {
                i += groupLength;
                continue;
            }

            current.Append(expression[i]);
            i++;
        }

        segments.Add(current.ToString());
        return (count, segments);
    }

    /// <summary>
    /// Returns the length of the parameter slot starting at <paramref name="index"/> in
    /// <paramref name="expression"/>, or 0 when none starts there. For a regex-authored binding
    /// any capturing group or <c>{…}</c> placeholder is a slot; for a Cucumber-authored binding
    /// only <c>{…}</c> placeholders are (a parenthesised group there is optional text — see
    /// <see cref="GroupLengthAt"/>). Delegates the detection rules to
    /// <see cref="StepExpressionParameters.SlotLengthAt"/> so the validator and the rest of the
    /// rename pipeline agree.
    /// </summary>
    private static int ParameterSlotLengthAt(string expression, int index, bool isCucumber)
    {
        var c = expression[index];

        if (c == '{')
            return StepExpressionParameters.SlotLengthAt(expression, index);

        if (c == '(' && !isCucumber)
            return StepExpressionParameters.SlotLengthAt(expression, index);

        return 0;
    }

    /// <summary>
    /// Returns the length of the parenthesised group starting at <paramref name="index"/>, or 0
    /// when the character is not an unescaped <c>(</c> or the group is not closed. A matched
    /// group is expression structure that <see cref="ScanExpression"/> skips without counting as
    /// a slot; an unmatched <c>(</c> is left in the static text where Rule 3 rejects it.
    /// </summary>
    private static int GroupLengthAt(string expression, int index)
    {
        if (expression[index] != '(' || StepExpressionParameters.IsEscaped(expression, index))
            return 0;

        var depth = 1;
        var j = index + 1;
        while (j < expression.Length && depth > 0)
        {
            if (expression[j] == '(' && !StepExpressionParameters.IsEscaped(expression, j)) depth++;
            else if (expression[j] == ')' && !StepExpressionParameters.IsEscaped(expression, j)) depth--;
            j++;
        }

        return depth == 0 ? j - index : 0;
    }
}

/// <summary>
/// Describes a validation failure. <see cref="Message"/> is a human-readable error
/// intended for display in the IDE rename dialog. <see cref="Scope"/> indicates
/// which validation stage produced the error ("position", "expression", "rename", "project").
/// </summary>
public sealed record ValidationError(string Message, string Scope);
