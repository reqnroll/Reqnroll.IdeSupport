using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.InlayHints;

/// <summary>Projects a feature file's binding match cache into inline hint annotations (F23).</summary>
public interface IInlayHintService
{
    /// <summary>
    /// Builds inlay hints for the steps in the given binding match set. When
    /// <paramref name="startLine"/>/<paramref name="endLine"/> are both given, steps whose line
    /// span doesn't overlap [<paramref name="startLine"/>, <paramref name="endLine"/>] are skipped
    /// before any hint is built for them — the actual cost reduction for a viewport-scoped
    /// <c>textDocument/inlayHint</c> request (issue #471): the caller previously built hints for
    /// every step in the document, then filtered the *output* by range.
    /// </summary>
    IReadOnlyList<GherkinInlayHint> Build(FeatureBindingMatchSet matchSet, int? startLine = null, int? endLine = null);
}
