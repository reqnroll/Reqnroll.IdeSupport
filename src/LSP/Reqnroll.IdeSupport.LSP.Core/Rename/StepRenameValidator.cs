#nullable enable

using System;
using System.Linq;
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
    /// character in a Cucumber Expression, unlike in a regex.
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
        // count as "operators" depends on the original binding's own syntax: a
        // literal '$' (e.g. a currency amount in "the price is ${float}") is meaningless
        // punctuation in a Cucumber Expression but a real anchor in a regex, so the two syntaxes
        // need different forbidden-character sets rather than one shared regex-operator list.
        var isCucumber = CucumberExpressionDetector.IsCucumberExpression(originalExpression);
        var originalParamCount = CountParameterSlots(originalExpression, isCucumber);
        var newParamCount = CountParameterSlots(newName, isCucumber);

        var forbiddenOperators = isCucumber ? CucumberExpressionOperators : RegexOperators;

        // Scan non-parameter segments for operators the rename INTRODUCES. An operator the
        // original expression already carries outside its parameter slots - most importantly the
        // ^...$ anchors of a regex binding - is not an introduction: Visual Studio's "Rename Step"
        // command seeds its prompt with the binding's own expression and submits the edited text
        // verbatim as newName, so those characters arrive without the user having typed them.
        // Rejecting on mere presence made every anchored regex binding unrenamable in Visual
        // Studio.
        var newNonParamSegments = SplitNonParameterSegments(newName, isCucumber);
        var originalNonParamSegments = SplitNonParameterSegments(originalExpression, isCucumber);
        foreach (var operatorChar in forbiddenOperators)
        {
            if (CountOccurrences(newNonParamSegments, operatorChar) > CountOccurrences(originalNonParamSegments, operatorChar))
                return new ValidationError("The non-parameter parts cannot contain expression operators", "rename");
        }

        // Rule 4: parameter count must match
        if (originalParamCount != newParamCount)
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
    /// Counts parameter slots in an expression. What is a slot depends on the binding's syntax
    /// (see <see cref="ScanExpression"/>).
    /// </summary>
    private static int CountParameterSlots(string expression, bool isCucumber)
        => string.IsNullOrEmpty(expression) ? 0 : ScanExpression(expression, isCucumber).Count(e => e.IsSlot);

    /// <summary>
    /// Splits an expression into non-parameter segments by removing its parameter slots and its
    /// other matched groups (see <see cref="ScanExpression"/>), so the delimiters of optional text
    /// or of a non-capturing group are never mistaken for operators in literal text.
    /// </summary>
    private static string[] SplitNonParameterSegments(string expression, bool isCucumber)
    {
        if (string.IsNullOrEmpty(expression))
            return Array.Empty<string>();

        var segments = new System.Collections.Generic.List<string>();
        var last = 0;
        foreach (var (start, length, _) in ScanExpression(expression, isCucumber))
        {
            if (start > last)
                segments.Add(expression.Substring(last, start - last));
            last = start + length;
        }
        if (last < expression.Length)
            segments.Add(expression.Substring(last));
        return segments.ToArray();
    }

    /// <summary>
    /// Finds the structural elements of <paramref name="expression"/>, left to right: <c>{…}</c>
    /// placeholders (including the empty <c>{}</c>), which are slots in either syntax, and
    /// parenthesised groups, matched with balanced parentheses so one containing nested groups
    /// (<c>(\d+(\.\d+)?)</c>) is a single element. Escaped parentheses are literal
    /// text and an unbalanced <c>(</c> is plain text.
    /// <para>
    /// Whether a group is a slot is syntax-aware: in a regex-authored binding only a
    /// capturing group is — non-capturing, look-around and inline-option groups are structure; in
    /// a Cucumber-authored binding <c>(text)</c> is optional text, never a parameter. Structure
    /// that is not a slot is still reported (<c>IsSlot == false</c>) so that its delimiters are
    /// excluded from the non-parameter text scanned for operators.
    /// </para>
    /// </summary>
    private static System.Collections.Generic.List<(int Start, int Length, bool IsSlot)> ScanExpression(
        string expression, bool isCucumber)
    {
        var elements = new System.Collections.Generic.List<(int Start, int Length, bool IsSlot)>();
        var i = 0;
        while (i < expression.Length)
        {
            var length = 0;
            var isSlot = false;
            if (expression[i] == '{')
            {
                length = StepExpressionParameters.SlotLengthAt(expression, i);
                isSlot = true;
            }
            else if (expression[i] == '(' && !StepExpressionParameters.IsEscaped(expression, i))
            {
                length = GroupLengthAt(expression, i);
                isSlot = !isCucumber && IsCapturingGroup(expression, i);
            }

            if (length > 0)
            {
                elements.Add((i, length, isSlot));
                i += length;
            }
            else
                i++;
        }
        return elements;
    }

    /// <summary>Returns the length of the balanced group opened at <paramref name="open"/>, or 0 when it is unbalanced.</summary>
    private static int GroupLengthAt(string expression, int open)
    {
        var depth = 0;
        for (var j = open; j < expression.Length; j++)
        {
            if (StepExpressionParameters.IsEscaped(expression, j))
                continue;
            if (expression[j] == '(') depth++;
            else if (expression[j] == ')' && --depth == 0) return j - open + 1;
        }
        return 0;
    }

    /// <summary>
    /// Whether the regex group opened at <paramref name="open"/> captures: a plain <c>(…)</c> or a
    /// named group (<c>(?&lt;name&gt;…)</c>, <c>(?'name'…)</c>); not <c>(?:</c>, <c>(?=</c>,
    /// <c>(?!</c>, <c>(?&lt;=</c>, <c>(?&lt;!</c> or an inline option such as <c>(?i)</c>.
    /// </summary>
    private static bool IsCapturingGroup(string expression, int open)
    {
        if (open + 1 >= expression.Length || expression[open + 1] != '?')
            return true;
        if (open + 3 >= expression.Length)
            return false;
        return expression[open + 2] == '\'' ||
               (expression[open + 2] == '<' && expression[open + 3] != '=' && expression[open + 3] != '!');
    }

    /// <summary>
    /// Counts occurrences of <paramref name="operatorChar"/> across the given non-parameter
    /// segments, so an operator the rename introduces can be told apart from one the original
    /// expression already carried.
    /// </summary>
    private static int CountOccurrences(string[] segments, char operatorChar)
        => segments.Sum(segment => segment.Count(c => c == operatorChar));
}

/// <summary>
/// Describes a validation failure. <see cref="Message"/> is a human-readable error
/// intended for display in the IDE rename dialog. <see cref="Scope"/> indicates
/// which validation stage produced the error ("position", "expression", "rename", "project").
/// </summary>
public sealed record ValidationError(string Message, string Scope);
