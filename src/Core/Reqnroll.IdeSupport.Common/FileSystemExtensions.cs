#nullable disable

namespace Reqnroll.IdeSupport.Common;

/// <summary>FileSystemExtensions</summary>
public static class FileSystemExtensions
{
    /// <summary>Returns <paramref name="filePath"/> if the file exists, otherwise <c>null</c>.</summary>
    public static string GetFilePathIfExists(this IFileSystemForIDE fileSystem, string filePath)
    {
        if (fileSystem.File.Exists(filePath))
            return filePath;
        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="extension"/> is <see langword="null"/>
    /// (matches anything) or <paramref name="filePath"/> ends with it (case-insensitive).
    /// </summary>
    public static bool IsOfType(this string filePath, string extension)
    {
        if (extension == null)
            return true;
        return filePath.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase);
    }
}
