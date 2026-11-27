using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;


/// <summary>
/// Persists the anonymous telemetry installation id, a GUID generated on first use, under
/// <c>%LOCALAPPDATA%\Reqnroll\installationid</c>.
/// </summary>
/// <remarks>
/// <para>
/// The file is in the local (machine-specific) profile, not the roaming one, so the id stays with the
/// installation on this machine instead of following the user to other machines.
/// </para>
/// <para>
/// <see cref="GetInstallationId"/> is called when the first permitted event is sent, so it must never
/// throw: a read-only, locked, unreachable or unset profile must not break telemetry. All file I/O is
/// therefore guarded, and when the profile cannot be read or written an in-memory GUID is used for the
/// session instead. The <see cref="Lazy{T}"/> must never observe an exception — a faulted
/// <see cref="Lazy{T}"/> caches and rethrows it, turning a transient I/O failure into a permanently
/// broken transmitter — so <see cref="FetchAndPersistInstallationId"/> swallows I/O failures internally.
/// </para>
/// </remarks>
[Export(typeof(IInstallationIdStore))]
public class FileInstallationIdStore : IInstallationIdStore
{
    private const string InstallationIdFileName = "installationid";

    /// <summary>
    /// Full path of the file that stores the installation id, or <see langword="null"/> when no usable
    /// local-profile directory is available (e.g. <c>%LOCALAPPDATA%</c> is unset or relative).
    /// </summary>
    public static readonly string? InstallationIdFilePath =
        ResolveInstallationIdFilePath(Environment.GetEnvironmentVariable("LOCALAPPDATA"));

    private readonly IFileSystemForIDE _fileSystem;

    private readonly Lazy<string> _lazyInstallationId;

    /// <summary>MEF importing constructor.</summary>
    [ImportingConstructor]
    public FileInstallationIdStore(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
        _lazyInstallationId = new Lazy<string>(FetchAndPersistInstallationId);
    }

    /// <summary>Returns the persisted installation id, generating and persisting a new one if none exists yet.</summary>
    public string GetInstallationId() => _lazyInstallationId.Value;

    /// <summary>
    /// Resolves the installation-id file path from the <c>%LOCALAPPDATA%</c> value, or returns
    /// <see langword="null"/> when it is unset or not an absolute path — expanding an unset
    /// environment variable would otherwise leave a literal, relative path such as
    /// <c>%LOCALAPPDATA%\Reqnroll\installationid</c>.
    /// </summary>
    internal static string? ResolveInstallationIdFilePath(string? localAppData)
        => string.IsNullOrEmpty(localAppData) || !Path.IsPathRooted(localAppData)
            ? null
            : Path.Combine(localAppData, "Reqnroll", InstallationIdFileName);

    private string FetchAndPersistInstallationId()
    {
        try
        {
            if (InstallationIdFilePath is not null && _fileSystem.File.Exists(InstallationIdFilePath))
            {
                var textFromFile = _fileSystem.File.ReadAllText(InstallationIdFilePath);
                if (TryParseValidGuid(textFromFile, out var persistedId)) return persistedId;
            }

            var newId = Guid.NewGuid().ToString();
            if (InstallationIdFilePath is not null) PersistInstallationId(InstallationIdFilePath, newId);
            return newId;
        }
        catch (Exception ex)
        {
            // A read-only, locked, unreachable or otherwise unusable profile must never break the
            // extension: fall back to an in-memory id for this session instead of faulting.
            Debug.WriteLine(ex, "Could not read or persist the telemetry installation id; using an in-memory id for this session.");
            return Guid.NewGuid().ToString();
        }
    }

    private void PersistInstallationId(string filePath, string installationId)
    {
        var directoryName = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directoryName) && !_fileSystem.Directory.Exists(directoryName))
            _fileSystem.Directory.CreateDirectory(directoryName);

        _fileSystem.File.WriteAllText(filePath, installationId);
    }

    /// <summary>
    /// Returns the canonical form of <paramref name="text"/> when it parses as a GUID, so padded or
    /// braced text read from the file is normalised before being used as the installation id.
    /// </summary>
    private static bool TryParseValidGuid(string text, out string installationId)
    {
        if (Guid.TryParse(text, out var parsedGuid))
        {
            installationId = parsedGuid.ToString();
            return true;
        }

        installationId = string.Empty;
        return false;
    }
}
