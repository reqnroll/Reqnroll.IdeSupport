#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// A single formatting edit: replaces the full content of lines <see cref="StartLine"/> through
/// <see cref="EndLine"/> (0-based, inclusive) with <see cref="NewText"/>.
/// </summary>
/// <remarks>
/// Every edit the Gherkin document formatter produces spans whole lines — column 0 through
/// end-of-line — so a line-number range is enough to apply it; no character offsets are needed.
/// </remarks>
public readonly record struct GherkinLineRangeEdit(int StartLine, int EndLine, string NewText);
