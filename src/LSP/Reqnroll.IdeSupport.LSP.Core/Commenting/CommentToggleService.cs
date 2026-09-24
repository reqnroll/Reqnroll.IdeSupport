namespace Reqnroll.IdeSupport.LSP.Core.Commenting;

/// <summary>
/// Toggles <c>#</c> comments on Gherkin feature file lines.
/// In <see cref="CommentToggleMode.Toggle"/> mode, if every line in the range is already
/// commented, all are uncommented; otherwise all are commented (regardless of per-line state).
/// <see cref="CommentToggleMode.Comment"/> and <see cref="CommentToggleMode.Uncomment"/> force
/// the direction.
/// </summary>
public class CommentToggleService : ICommentToggleService
{
    private const char CommentChar = '#';

    /// <summary>Changes line comments for the given line range according to <paramref name="mode"/>.</summary>
    public GherkinCommentToggleResult ToggleComment(
        string documentText,
        int rangeStartLine,
        int rangeEndLine,
        CommentToggleMode mode = CommentToggleMode.Toggle)
    {
        var lines = SplitLines(documentText);

        var uncomment = mode switch
        {
            CommentToggleMode.Comment   => false,
            CommentToggleMode.Uncomment => true,
            _                           => AreAllCommented(lines, rangeStartLine, rangeEndLine),
        };

        var edits = new List<GherkinCommentEdit>();
        for (int i = rangeStartLine; i <= rangeEndLine && i < lines.Length; i++)
        {
            var line = lines[i];
            var newLine = uncomment ? UncommentLine(line) : CommentLine(line);

            // An explicit Uncomment leaves uncommented lines alone; don't emit no-op edits for them.
            if (mode == CommentToggleMode.Uncomment && newLine == line)
                continue;

            edits.Add(new GherkinCommentEdit(i, i, newLine));
        }

        return new GherkinCommentToggleResult(edits.AsReadOnly());
    }

    private static bool AreAllCommented(string[] lines, int rangeStartLine, int rangeEndLine)
    {
        var allCommented = true;

        // Determine if ALL non-empty lines in range are commented.
        // Empty lines count as "not commented" for toggle determination.
        for (int i = rangeStartLine; i <= rangeEndLine && i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0)
            {
                allCommented = false;
                break;
            }
            if (trimmed[0] != CommentChar)
            {
                allCommented = false;
                break;
            }
        }

        return allCommented;
    }

    private static string CommentLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return CommentChar.ToString();
        return CommentChar + " " + line;
    }

    private static string UncommentLine(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(CommentChar.ToString()))
            return line;

        var leadingSpaces = line.Length - trimmed.Length;
        var afterHash = trimmed.Substring(1); // remove the #

        // Remove one following space if present
        var content = afterHash.StartsWith(" ") ? afterHash.Substring(1) : afterHash;

        return new string(' ', leadingSpaces) + content;
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');
}
