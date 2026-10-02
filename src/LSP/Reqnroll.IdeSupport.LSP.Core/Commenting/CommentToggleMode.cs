namespace Reqnroll.IdeSupport.LSP.Core.Commenting;

/// <summary>
/// How <see cref="ICommentToggleService.ToggleComment"/> changes the comment state of a line range.
/// </summary>
public enum CommentToggleMode
{
    /// <summary>
    /// Uncomments the range if every line is already commented; otherwise comments every line.
    /// </summary>
    Toggle,

    /// <summary>
    /// Always comments every line, adding another <c>#</c> to lines that are already commented
    /// (Visual Studio's <c>Edit.CommentSelection</c> semantics).
    /// </summary>
    Comment,

    /// <summary>
    /// Removes one level of <c>#</c> from every commented line, leaving other lines unchanged
    /// (Visual Studio's <c>Edit.UncommentSelection</c> semantics).
    /// </summary>
    Uncomment,
}
