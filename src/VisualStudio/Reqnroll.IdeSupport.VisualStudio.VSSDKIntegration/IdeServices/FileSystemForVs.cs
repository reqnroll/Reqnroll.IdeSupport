#nullable enable
using Reqnroll.IdeSupport.Common;
using System.ComponentModel.Composition;
using System.IO.Abstractions;

namespace Reqnroll.IdeSupport.VisualStudio.IdeServices;

// We cannot directly use IFileSystem as dependency (with MEF), because there might be other extensions (e.g. SpecFlow)
// that also export an implementation of IFileSystem. We need to have a separate contract for "our" file system.


/// <summary>
/// Visual Studio's MEF-exported <see cref="IFileSystemForIDE"/> implementation; kept as a
/// dedicated type (rather than exporting <see cref="System.IO.Abstractions.IFileSystem"/> directly)
/// so it does not collide with other extensions (e.g. SpecFlow) that export their own.
/// </summary>
[Export(typeof(IFileSystemForIDE))]
public class FileSystemForVs : FileSystemForIDE
{
}
