namespace Reqnroll.IdeSupport.LSP.Core.Commenting;

/// <summary>
/// Protocol-agnostic edit describing a single line's replacement text.
/// Both lines are 0-based inclusive.
/// </summary>
public sealed record GherkinCommentEdit(
    int StartLine,
    int EndLine,
    string NewText);

/// <summary>
/// The result of a comment-toggle operation: a set of per-line text replacements.
/// <paramref name="Uncommented"/> is the direction the request resolved to: <c>true</c> when it
/// removed comments, <c>false</c> when it added them. It is how a Toggle's effective outcome is
/// reported (telemetry), since the edits alone can be empty.
/// </summary>
public sealed record GherkinCommentToggleResult(
    IReadOnlyList<GherkinCommentEdit> Edits,
    bool Uncommented = false);
