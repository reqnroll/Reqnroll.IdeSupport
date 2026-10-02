#nullable enable

using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;

/// <summary>
/// Builds the Find All References window title shown when Go To Definition on a step finds several
/// step definitions (issue #757), e.g. <c>Reqnroll: 2 step definitions for 'Given the first number is 50'</c>.
/// </summary>
/// <remarks>
/// Replaces VS's own <c>'{word}' declarations</c> title, which names only the word under the caret.
/// The step is taken from the caret's line: the server resolves a definition only when the caret is
/// on a step line itself (never a table row or doc string), so that line is always the step.
/// </remarks>
internal static class StepDefinitionsWindowTitle
{
    /// <summary>Steps longer than this are cut short with an ellipsis so the tool-window tab stays readable.</summary>
    internal const int MaxStepLength = 80;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Builds the title for <paramref name="count"/> definitions of the step on <paramref name="caretLineText"/>.</summary>
    public static string Build(string caretLineText, int count)
    {
        var step = Whitespace.Replace(caretLineText, " ").Trim();
        if (step.Length > MaxStepLength)
            step = step.Substring(0, MaxStepLength - 1).TrimEnd() + "…";

        var noun = count == 1 ? "step definition" : "step definitions";
        return step.Length == 0
            ? $"Reqnroll: {count} {noun}"
            : $"Reqnroll: {count} {noun} for '{step}'";
    }
}
