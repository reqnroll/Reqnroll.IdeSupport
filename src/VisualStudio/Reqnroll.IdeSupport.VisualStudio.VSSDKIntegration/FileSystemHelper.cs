#nullable disable

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Helper methods for matching file paths against extensions.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="extension"/> is <see langword="null"/>
    /// (matches anything) or <paramref name="filePath"/> ends with it (case-insensitive).
    /// </summary>
    public static bool IsOfType(string filePath, string extension)
    {
        if (extension == null)
            return true;
        return filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }
}