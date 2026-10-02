namespace Reqnroll.IdeSupport.LSP.Core.Commenting;

/// <summary>
/// Toggles Gherkin comments (<c>#</c>) on a range of lines in a feature-file document.
/// </summary>
public interface ICommentToggleService
{
    /// <summary>
    /// Changes the comment state for lines <paramref name="rangeStartLine"/> to
    /// <paramref name="rangeEndLine"/> (0-based, inclusive) according to <paramref name="mode"/>.
    /// With <see cref="CommentToggleMode.Toggle"/>: if ALL lines in the range are commented
    /// (start with <c>#</c>), they are uncommented. Otherwise ALL lines are commented.
    /// </summary>
    GherkinCommentToggleResult ToggleComment(
        string documentText,
        int rangeStartLine,
        int rangeEndLine,
        CommentToggleMode mode = CommentToggleMode.Toggle);
}
