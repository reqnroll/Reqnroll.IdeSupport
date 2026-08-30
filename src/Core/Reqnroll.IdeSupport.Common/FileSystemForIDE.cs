using System;
using System.IO.Abstractions;

namespace Reqnroll.IdeSupport.Common
{
    /// <summary>FileSystemForIDE</summary>
    public class FileSystemForIDE : IFileSystemForIDE
    {
        private readonly FileSystem _fileSystem = new();

        /// <summary>Gets the underlying <see cref="IFile"/> abstraction.</summary>
        public IFile File => _fileSystem.File;
        /// <summary>Gets the underlying <see cref="IDirectory"/> abstraction.</summary>
        public IDirectory Directory => _fileSystem.Directory;
        /// <summary>Gets the underlying <see cref="IFileInfoFactory"/> abstraction.</summary>
        public IFileInfoFactory FileInfo => _fileSystem.FileInfo;
        /// <summary>Gets the underlying <see cref="IFileStreamFactory"/> abstraction.</summary>
        public IFileStreamFactory FileStream => _fileSystem.FileStream;
        /// <summary>Gets the underlying <see cref="IPath"/> abstraction.</summary>
        public IPath Path => _fileSystem.Path;
        /// <summary>Gets the underlying <see cref="IDirectoryInfoFactory"/> abstraction.</summary>
        public IDirectoryInfoFactory DirectoryInfo => _fileSystem.DirectoryInfo;
        /// <summary>Gets the underlying <see cref="IDriveInfoFactory"/> abstraction.</summary>
        public IDriveInfoFactory DriveInfo => _fileSystem.DriveInfo;
        /// <summary>Gets the underlying <see cref="IFileSystemWatcherFactory"/> abstraction.</summary>
        public IFileSystemWatcherFactory FileSystemWatcher => _fileSystem.FileSystemWatcher;

        /// <summary>Not supported by this implementation; always throws <see cref="NotImplementedException"/>.</summary>
        public IFileVersionInfoFactory FileVersionInfo => throw new NotImplementedException();
    }
}
