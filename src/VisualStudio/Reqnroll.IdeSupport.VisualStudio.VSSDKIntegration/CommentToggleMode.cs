#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Which built-in editor command asked for a comment change in a <c>.feature</c> file; maps to the
/// optional <c>mode</c> argument of the server's <c>reqnroll.toggleComment</c> command.
/// </summary>
public enum CommentToggleMode
{
    /// <summary><c>Edit.ToggleLineComment</c>: uncomment if every line is commented, else comment.</summary>
    Toggle,

    /// <summary><c>Edit.CommentSelection</c>: always add a <c>#</c>.</summary>
    Comment,

    /// <summary><c>Edit.UncommentSelection</c>: remove one <c>#</c> from each commented line.</summary>
    Uncomment,
}
