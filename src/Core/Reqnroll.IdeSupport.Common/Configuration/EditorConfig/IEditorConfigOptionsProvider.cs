namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Resolves the EditorConfig options that apply to a given file-system path.
/// </summary>
public interface IEditorConfigOptionsProvider
{
    /// <summary>
    /// Returns the merged EditorConfig settings that apply to <paramref name="filePath"/>,
    /// following the standard search-upward-from-file-directory semantics.
    /// </summary>
    IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath);

    /// <summary>
    /// Evicts the cached parse result for the given <c>.editorconfig</c> file so that the
    /// next call to <see cref="GetEditorConfigOptionsByPath"/> re-reads it from disk.
    /// No-op when no cache entry exists.
    /// </summary>
    void InvalidateCache(string editorConfigFilePath);
}
