#nullable enable

using System;
using System.Linq;
using System.Text.RegularExpressions;
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

    private static readonly Regex ParameterSlotPattern = new(
        @"(\([^)]*\)|\{\w+\})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        var originalParamCount = CountParameterSlots(originalExpression);
        var newParamCount = CountParameterSlots(newName);

        var forbiddenOperators = CucumberExpressionDetector.IsCucumberExpression(originalExpression)
            ? CucumberExpressionOperators
            : RegexOperators;

        // Scan non-parameter segments for operators
        var newNonParamSegments = SplitNonParameterSegments(newName);
        foreach (var segment in newNonParamSegments)
        {
            if (segment.IndexOfAny(forbiddenOperators) >= 0)
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
    /// Counts parameter slots in an expression. Parameter slots are either
    /// regex capture groups like <c>(.*)</c> or Cucumber placeholders like <c>{word}</c>.
    /// </summary>
    private static int CountParameterSlots(string expression)
    {
        if (string.IsNullOrEmpty(expression))
            return 0;
        return ParameterSlotPattern.Matches(expression).Count;
    }

    /// <summary>
    /// Splits an expression into non-parameter segments by removing parameter slots.
    /// </summary>
    private static string[] SplitNonParameterSegments(string expression)
    {
        return string.IsNullOrEmpty(expression)
            ? Array.Empty<string>()
            : ParameterSlotPattern.Split(expression)
                .Where(s => !string.IsNullOrEmpty(s) && !ParameterSlotPattern.IsMatch(s))
                .ToArray();
    }
}

/// <summary>
/// Describes a validation failure. <see cref="Message"/> is a human-readable error
/// intended for display in the IDE rename dialog. <see cref="Scope"/> indicates
/// which validation stage produced the error ("position", "expression", "rename", "project").
/// </summary>
public sealed record ValidationError(string Message, string Scope);
