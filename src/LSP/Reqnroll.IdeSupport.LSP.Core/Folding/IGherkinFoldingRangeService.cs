using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Folding;

/// <summary>
/// Computes foldable regions from the IdeSupportTag tree.
/// </summary>
public interface IGherkinFoldingRangeService
{
    /// <summary>
    /// Returns a list of folding ranges for the given feature-file tag tree.
    /// Returns an empty list when no feature is present.
    /// </summary>
    IReadOnlyList<GherkinFoldingRange> BuildFoldingRanges(IReadOnlyCollection<IdeSupportTag> tags);
}
