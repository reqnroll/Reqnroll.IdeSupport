#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// An <see cref="ISourceFileResolver"/> that only ever confirms a path that already exists —
/// no remapping. The default for callers with no project context (unit tests, and any importer
/// constructed without a resolver), so behaviour there is exactly what it was before #540.
/// </summary>
public sealed class LocalOnlySourceFileResolver : ISourceFileResolver
{
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Creates a resolver that performs only an existence check.</summary>
    public LocalOnlySourceFileResolver(IFileSystemForIDE fileSystem) => _fileSystem = fileSystem;

    /// <inheritdoc/>
    public string? Resolve(string? recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath))
            return null;

        try { return _fileSystem.File.Exists(recordedPath) ? recordedPath : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        { return null; }
    }
}
