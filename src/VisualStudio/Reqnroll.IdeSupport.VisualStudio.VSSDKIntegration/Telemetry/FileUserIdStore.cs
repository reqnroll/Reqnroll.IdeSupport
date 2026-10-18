using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;


/// <summary>
/// Persists a stable, anonymous per-user GUID under <c>%APPDATA%\Reqnroll\userid</c> for telemetry
/// correlation, generating one on first use.
/// </summary>
/// <remarks>
/// This store is constructed by MEF inside <c>TelemetryTransmitter</c>'s importing constructor, so
/// it must never throw: a read-only, locked, unreachable or unset profile must not fault the
/// telemetry chain (#1029). All file I/O is therefore guarded, and when the profile cannot be read
/// or written an in-memory GUID is used for the session instead. The <see cref="Lazy{T}"/> must
/// never observe an exception — a faulted <see cref="Lazy{T}"/> caches and rethrows it, turning a
/// transient I/O failure into a permanently broken transmitter — so
/// <see cref="FetchAndPersistUserId"/> swallows I/O failures internally.
/// </remarks>
[Export(typeof(IUserUniqueIdStore))]
public class FileUserIdStore : IUserUniqueIdStore
{
    private const string UserIdFileName = "userid";

    /// <summary>
    /// Full path of the file that stores the persisted user id, or <see langword="null"/> when no
    /// usable roaming-profile directory is available (e.g. <c>%APPDATA%</c> is unset or relative).
    /// </summary>
    public static readonly string? UserIdFilePath = ResolveUserIdFilePath(Environment.GetEnvironmentVariable("APPDATA"));

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

    /// <summary>
    /// Resolves the user-id file path from the <c>%APPDATA%</c> value, or returns
    /// <see langword="null"/> when it is unset or not an absolute path — expanding an unset
    /// environment variable would otherwise leave a literal, relative path such as
    /// <c>%APPDATA%\Reqnroll\userid</c>.
    /// </summary>
    internal static string? ResolveUserIdFilePath(string? appData)
        => string.IsNullOrEmpty(appData) || !Path.IsPathRooted(appData)
            ? null
            : Path.Combine(appData, "Reqnroll", UserIdFileName);

    private string FetchAndPersistUserId()
    {
        try
        {
            if (UserIdFilePath is not null && _fileSystem.File.Exists(UserIdFilePath))
            {
                var userIdStringFromFile = _fileSystem.File.ReadAllText(UserIdFilePath);
                if (TryParseValidGuid(userIdStringFromFile, out var persistedUserId)) return persistedUserId;
            }

            var newUserId = Guid.NewGuid().ToString();
            if (UserIdFilePath is not null) PersistUserId(UserIdFilePath, newUserId);
            return newUserId;
        }
        catch (Exception ex)
        {
            // A read-only, locked, unreachable or otherwise unusable profile must never break the
            // extension: fall back to an in-memory id for this session instead of faulting.
            Debug.WriteLine(ex, "Could not read or persist the telemetry user id; using an in-memory id for this session.");
            return Guid.NewGuid().ToString();
        }
    }

    private void PersistUserId(string filePath, string userId)
    {
        var directoryName = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directoryName) && !_fileSystem.Directory.Exists(directoryName))
            _fileSystem.Directory.CreateDirectory(directoryName);

        _fileSystem.File.WriteAllText(filePath, userId);
    }

    /// <summary>
    /// Returns the canonical form of <paramref name="text"/> when it parses as a GUID, so padded or
    /// braced text read from the file is normalised before being used as the user id.
    /// </summary>
    private static bool TryParseValidGuid(string text, out string userId)
    {
        if (Guid.TryParse(text, out var parsedGuid))
        {
            userId = parsedGuid.ToString();
            return true;
        }

        userId = string.Empty;
        return false;
    }
}
