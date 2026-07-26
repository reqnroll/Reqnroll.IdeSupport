using Microsoft.Win32;
using Reqnroll.IdeSupport.Common.Telemetry;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.Telemetry;

/// <summary>
/// Reads and writes the Reqnroll installation-status registry key (<c>HKCU\Software\Reqnroll\VSLSP</c>)
/// used to track install date, last-used date, usage days, and user level.
/// </summary>
[Export(typeof(IRegistryManager))]
public class RegistryManager : IRegistryManager
{
#if DEBUG
    private static string RegPath => @"Software\Reqnroll\VSLSP\Debug";
#else
    private static string RegPath => @"Software\Reqnroll\VSLSP";
#endif

    private const string Version = "version.vs2022";
    private const string InstallDate = "installDate.vs2022";
    private const string LastUsedDate = "lastUsedDate";
    private const string UsageDays = "usageDays";
    private const string UserLevel = "userLevel";

    /// <summary>Reads the persisted installation status from the registry, or a blank status if the key is missing/unreadable.</summary>
    public ReqnrollInstallationStatus GetInstallStatus()
    {
        var status = new ReqnrollInstallationStatus();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, RegistryKeyPermissionCheck.ReadSubTree);

            status.InstalledVersion = ReadVersion(key, Version);
            status.InstallDate = ReadDate(key, InstallDate);
            status.LastUsedDate = ReadDate(key, LastUsedDate);
            status.UsageDays = ReadIntValue(key, UsageDays);
            status.UserLevel = ReadIntValue(key, UserLevel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"Registry read error: {this}");
        }

        return status;
    }

    /// <summary>Writes <paramref name="status"/> to the registry; returns <see langword="false"/> if the key could not be opened/created.</summary>
    public bool UpdateStatus(ReqnrollInstallationStatus status)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegPath, RegistryKeyPermissionCheck.ReadWriteSubTree);

        if (key == null)
            return false;

        if (status.InstalledVersion != null)
            key.SetValue(Version, status.InstalledVersion);
        if (status.InstallDate != ReqnrollInstallationStatus.MagicDate)
            key.SetValue(InstallDate, SerializeDate(status.InstallDate));
        if (status.LastUsedDate != ReqnrollInstallationStatus.MagicDate)
            key.SetValue(LastUsedDate, SerializeDate(status.LastUsedDate));
        key.SetValue(UsageDays, status.UsageDays);
        key.SetValue(UserLevel, status.UserLevel);

        return true;
    }

    private Version ReadVersion(RegistryKey key, string name)
    {
        if (key.GetValue(name) is string value)
            return new Version(value);
        return ReqnrollInstallationStatus.UnknownVersion;
    }

    private DateTime ReadDate(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value is int intVal ? DeserializeDate(intVal) : ReqnrollInstallationStatus.MagicDate;
    }

    private int ReadIntValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value is int intVal ? intVal : 0;
    }

    private DateTime DeserializeDate(int days)
    {
        if (days <= 0)
            return ReqnrollInstallationStatus.MagicDate;
        return ReqnrollInstallationStatus.MagicDate.AddDays(days);
    }

    private int SerializeDate(DateTime dateTime) =>
        (int) dateTime.Date.Subtract(ReqnrollInstallationStatus.MagicDate).TotalDays;
}
