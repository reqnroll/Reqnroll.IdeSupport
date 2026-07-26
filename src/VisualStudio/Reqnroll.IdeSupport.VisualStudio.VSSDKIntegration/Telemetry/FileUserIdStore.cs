using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;


/// <summary>
/// Persists a stable, anonymous per-user GUID under <c>%APPDATA%\Reqnroll\userid</c> for telemetry
/// correlation, generating one on first use.
/// </summary>
[Export(typeof(IUserUniqueIdStore))]
public class FileUserIdStore : IUserUniqueIdStore
{
    /// <summary>Full path of the file that stores the persisted user id.</summary>
    public static readonly string UserIdFilePath = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Reqnroll\userid");
    private readonly IFileSystemForIDE _fileSystem;

    private readonly Lazy<string> _lazyUniqueUserId;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public FileUserIdStore(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
        _lazyUniqueUserId = new Lazy<string>(FetchAndPersistUserId);
    }

    /// <summary>Returns the persisted user id, generating and persisting a new one if none exists yet.</summary>
    public string GetUserId() => _lazyUniqueUserId.Value;

    private string FetchAndPersistUserId()
    {
        if (_fileSystem.File.Exists(UserIdFilePath))
        {
            var userIdStringFromFile = _fileSystem.File.ReadAllText(UserIdFilePath);
            if (IsValidGuid(userIdStringFromFile)) return userIdStringFromFile;
        }

        return GenerateAndPersistUserId();
    }

    private void PersistUserId(string userId)
    {
        var directoryName = Path.GetDirectoryName(UserIdFilePath);
        if (!_fileSystem.Directory.Exists(directoryName)) _fileSystem.Directory.CreateDirectory(directoryName);

        _fileSystem.File.WriteAllText(UserIdFilePath, userId);
    }

    private bool IsValidGuid(string guid) => Guid.TryParse(guid, out var parsedGuid);

    private string GenerateAndPersistUserId()
    {
        var newUserId = Guid.NewGuid().ToString();

        PersistUserId(newUserId);

        return newUserId;
    }
}
