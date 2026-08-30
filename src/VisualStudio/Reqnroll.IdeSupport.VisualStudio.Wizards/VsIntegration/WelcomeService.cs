// VsIntegration layer — VS SDK references are expected here.
using Microsoft.Win32;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Orchestrates the first-install welcome flow and the upgrade/changelog flow.
/// Ported from Reqnroll.VisualStudio\ProjectSystem\WelcomeService.cs.
///
/// Uses IWizardDialogService instead of the legacy IIdeSupportWindowManager pattern.
/// Called from ReqnrollPluginPackage.InitializeAsync after solution load.
/// </summary>
public class WelcomeService : IWelcomeService
{
    private readonly IRegistryManager _registryManager;
    private readonly IVersionProvider _versionProvider;
    private readonly IWizardDialogService _dialogService;
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Creates the service with its registry, version, dialog and file-system dependencies.</summary>
    public WelcomeService(
        IRegistryManager registryManager,
        IVersionProvider versionProvider,
        IWizardDialogService dialogService,
        IFileSystemForIDE fileSystem)
    {
        _registryManager = registryManager;
        _versionProvider = versionProvider;
        _dialogService = dialogService;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Called when an IDE scope becomes active. Shows the welcome dialog on first install,
    /// or the upgrade/changelog dialog when the installed version has changed, and
    /// updates install/usage status in the registry.
    /// </summary>
    public void OnIdeScopeActivityStarted(IIdeScope ideScope)
    {
        var telemetryService = ideScope.TelemetryService;
        var today = DateTime.Today;
        var status = _registryManager.GetInstallStatus();
        var currentVersion = new Version(_versionProvider.GetExtensionVersion());

        if (!status.IsInstalled)
        {
            // New user — first install
            telemetryService.MonitorExtensionInstalled();

            status.InstallDate = today;
            status.InstalledVersion = currentVersion;
            status.LastUsedDate = today;

            _registryManager.UpdateStatus(status);
            CheckFileAssociation(ideScope);

            ScheduleAndShow(() =>
            {
                _dialogService.ShowWelcomeDialog();
                telemetryService.MonitorWelcomeDialogDismissed(
                    new Dictionary<string, object>
                    {
                        { "ExtensionVersion", currentVersion.ToString() },
                    });
            });
        }
        else
        {
            if (status.LastUsedDate != today)
            {
                // A shiny new day with Reqnroll
                status.UsageDays++;
                status.LastUsedDate = today;
                _registryManager.UpdateStatus(status);
                telemetryService.MonitorExtensionDaysOfUsage(status.UsageDays);
            }

            if (status.InstalledVersion < currentVersion)
            {
                // Upgrading user
                var installedVersion = status.InstalledVersion.ToString();
                telemetryService.MonitorExtensionUpgraded(installedVersion);

                status.InstallDate = today;
                status.InstalledVersion = currentVersion;

                _registryManager.UpdateStatus(status);
                CheckFileAssociation(ideScope);

                var changeLog = GetSelectedChangeLog(installedVersion);
                var changeLogToShow = string.IsNullOrEmpty(changeLog)
                    ? string.Empty
                    : changeLog;

                ScheduleAndShow(() =>
                {
                    _dialogService.ShowUpgradeDialog(
                        currentVersion.ToString(),
                        changeLogToShow);
                    telemetryService.MonitorUpgradeDialogDismissed(
                        new Dictionary<string, object>
                        {
                            { "OldExtensionVersion", installedVersion },
                            { "NewExtensionVersion", currentVersion.ToString() },
                        });
                });
            }
        }
    }

    protected virtual void ScheduleAndShow(Action showAction)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(7)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            showAction();
        };

        timer.Start();
    }

    protected virtual void CheckFileAssociation(IIdeScope ideScope)
    {
        try
        {
            var extensionFolder = Path.GetDirectoryName(
                typeof(WelcomeService).Assembly.Location);
            if (extensionFolder is null) return;

            var iconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Reqnroll", "gherkin_reqnrollvs.ico");
            var sourceIcon = Path.Combine(extensionFolder, "Package", "Resources",
                "gherkin_reqnrollvs.ico");

            SetFileAssociation(iconPath, sourceIcon);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "CheckFileAssociation failed");
        }
    }

    private static void SetFileAssociation(string iconPath, string sourceIcon)
    {
        // Ported from WindowsFileAssociationDetector.SetAssociation
        const string progId = "Reqnroll.GherkinFile";
        const string fileExtension = ".feature";
        const string friendlyTypeName = "Gherkin Specification File for Reqnroll";

        string appPath = Process.GetCurrentProcess().MainModule.FileName;
        const string classesBaseKey = @"Software\Classes\" + progId;

        using (var key = Registry.CurrentUser.CreateSubKey(classesBaseKey,
                   RegistryKeyPermissionCheck.ReadWriteSubTree))
        {
            if (key == null) return;
            key.SetValue(null, friendlyTypeName);
            key.SetValue("FriendlyTypeName", friendlyTypeName);
        }

        // Copy icon if not exists
        if (!File.Exists(iconPath) && File.Exists(sourceIcon))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath));
            File.Copy(sourceIcon, iconPath, true);
        }

        if (File.Exists(iconPath))
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                classesBaseKey + @"\DefaultIcon",
                RegistryKeyPermissionCheck.ReadWriteSubTree);
            key?.SetValue(null, iconPath);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(
                   classesBaseKey + @"\shell",
                   RegistryKeyPermissionCheck.ReadWriteSubTree))
        {
            key?.SetValue(null, "Open");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(
                   classesBaseKey + @"\shell\open",
                   RegistryKeyPermissionCheck.ReadWriteSubTree))
        {
            key?.SetValue(null, "&Open");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(
                   classesBaseKey + @"\shell\open\command",
                   RegistryKeyPermissionCheck.ReadWriteSubTree))
        {
            key?.SetValue(null, string.Format(@"""{0}"" /edit ""%1""", appPath));
        }

        using (var key = Registry.CurrentUser.CreateSubKey(
                   @"Software\Classes\" + fileExtension,
                   RegistryKeyPermissionCheck.ReadWriteSubTree))
        {
            if (key == null) return;
            key.SetValue(null, progId);
            key.SetValue("Content Type", "application/text");
        }
    }

    protected virtual string GetSelectedChangeLog(string oldExtensionVersion)
    {
        string changeLog = GetChangeLog();
        if (string.IsNullOrEmpty(changeLog))
            return string.Empty;

        int start = 0;
        var newVersionMatch = Regex.Match(changeLog,
            @"^# v" + _versionProvider.GetExtensionVersion(),
            RegexOptions.Multiline);
        if (newVersionMatch.Success)
            start = newVersionMatch.Index;

        int end = changeLog.Length;
        if (oldExtensionVersion != null)
        {
            var oldVersionMatch = Regex.Match(changeLog,
                @"^# v" + oldExtensionVersion, RegexOptions.Multiline);
            if (oldVersionMatch.Success)
                end = oldVersionMatch.Index;
        }

        return changeLog.Substring(start, end - start);
    }

    protected virtual string GetChangeLog()
    {
        try
        {
            var extensionFolder = Path.GetDirectoryName(
                typeof(WelcomeService).Assembly.Location);
            if (extensionFolder is null) return string.Empty;

            var changeLogPath = Path.Combine(extensionFolder, "CHANGELOG.txt");
            if (!File.Exists(changeLogPath))
                return string.Empty;

            return File.ReadAllText(changeLogPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, "GetChangeLog failed");
            return string.Empty;
        }
    }
}
