#nullable disable

using Reqnroll.IdeSupport.Common.Logging;
using System;
using System.Threading;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Settings;

/// <summary>ProjectSettingsProvider</summary>
public class ProjectSettingsProvider : IDisposable, IProjectSettingsProvider
{
    /// <summary>Maximum number of times to retry initializing project settings before giving up.</summary>
    public const int MAX_RETRY_COUNT = 5;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private readonly IProjectScope _projectScope;
    private readonly ReqnrollProjectSettingsProvider _reqnrollProjectSettingsProvider;
    private readonly TimeSpan _retryDelay;
    private readonly SynchronizationContext _synchronizationContext;
    private ProjectSettings _projectSettings;
    private int _retryInitializeCounter;
    private Timer _retryInitializeTimer;
    private volatile bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ProjectSettingsProvider"/> class.</summary>
    public ProjectSettingsProvider( IProjectScope projectScope,
        ReqnrollProjectSettingsProvider reqnrollProjectSettingsProvider)
        : this(projectScope, reqnrollProjectSettingsProvider, DefaultRetryDelay)
    {
    }

    /// <summary>Test seam: allows the retry timer's delay to be shortened so the retry state machine can be exercised without waiting on the real 5-second interval.</summary>
    internal ProjectSettingsProvider(IProjectScope projectScope,
        ReqnrollProjectSettingsProvider reqnrollProjectSettingsProvider,
        TimeSpan retryDelay)
    {
        _projectScope = projectScope ?? throw new ArgumentNullException(nameof(projectScope));
        _reqnrollProjectSettingsProvider = reqnrollProjectSettingsProvider ??
                                           throw new ArgumentNullException(nameof(reqnrollProjectSettingsProvider));
        _retryDelay = retryDelay;
        // The project scope may be thread-affine (VsProjectScope requires the VS UI thread, which
        // is where this provider is constructed), so retries are marshalled back to this context
        // instead of running on the timer's thread-pool thread (#962).
        _synchronizationContext = SynchronizationContext.Current;
        InitializeProjectSettings();

        //_projectScope.GetIdeSupportConfigurationProvider().WeakConfigurationChanged += OnConfigurationChanged;
        //_projectScope.IdeScope.WeakProjectsBuilt += ProjectSystemOnProjectsBuilt;
    }

    private IIdeSupportLogger Logger => _projectScope.IdeScope.Logger;

    /// <summary>Stops the retry timer and releases resources held by this provider.</summary>
    public void Dispose()
    {
        _disposed = true;
        StopRetryInitializeTimer();
        //_projectScope.GetIdeSupportConfigurationProvider().WeakConfigurationChanged -= OnConfigurationChanged;
        //_projectScope.IdeScope.WeakProjectsBuilt -= ProjectSystemOnProjectsBuilt;
    }

    //public event EventHandler<EventArgs> SettingsInitialized;

    //public event EventHandler<EventArgs> WeakSettingsInitialized
    //{
    //    add => WeakEventManager<IProjectSettingsProvider, EventArgs>.AddHandler(this, nameof(SettingsInitialized),
    //        value);
    //    remove => WeakEventManager<IProjectSettingsProvider, EventArgs>.RemoveHandler(this, nameof(SettingsInitialized),
    //        value);
    //}

    /// <summary>Returns the currently cached project settings.</summary>
    public ProjectSettings GetProjectSettings() => _projectSettings;

    /// <summary>Re-loads project settings from the project system and updates the cache if they changed.</summary>
    public ProjectSettings CheckProjectSettings()
    {
        var projectSettings = LoadProjectSettings();
        if (projectSettings.IsUninitialized)
            return _projectSettings;

        if (projectSettings.Equals(_projectSettings))
            return _projectSettings;

        var wasUninitialized = _projectSettings.IsUninitialized;
        _projectSettings = projectSettings;
        if (wasUninitialized)
            OnSettingsInitialized(projectSettings);
        else
            Logger.LogInfo($"Project settings updated: {projectSettings.GetShortLabel()}");
        return _projectSettings;
    }

    private void InitializeProjectSettings()
    {
        _projectSettings = LoadProjectSettings();
        if (!_projectSettings.IsUninitialized)
            OnSettingsInitialized(_projectSettings);
        else
            StartRetryInitializeTimer();
    }

    private void StartRetryInitializeTimer()
    {
        _retryInitializeCounter++;
        Logger.LogInfo($"Project settings not available yet, retry in {_retryDelay.TotalSeconds} seconds...");
        _retryInitializeTimer = new Timer(RetryInitializeTimerTick, null, _retryDelay, Timeout.InfiniteTimeSpan);

    }

    private void StopRetryInitializeTimer()
    {
        _retryInitializeTimer?.Dispose();
        _retryInitializeTimer = null;
    }

    private void RetryInitializeTimerTick(object state)
    {
        // Runs on a thread-pool thread: an exception escaping here would terminate the host process.
        try
        {
            if (_synchronizationContext != null)
                _synchronizationContext.Post(_ => RetryInitialize(), null);
            else
                RetryInitialize();
        }
        catch (Exception ex)
        {
            Logger.LogException(_projectScope.IdeScope.TelemetryService, ex, "Project settings retry could not be scheduled");
        }
    }

    private void RetryInitialize()
    {
        StopRetryInitializeTimer();

        if (_disposed || !_projectSettings.IsUninitialized)
            return;

        try
        {
            CheckProjectSettings();
        }
        catch (Exception ex)
        {
            Logger.LogException(_projectScope.IdeScope.TelemetryService, ex, "Project settings retry failed");
        }

        if (_projectSettings.IsUninitialized)
        {
            if (_retryInitializeCounter < MAX_RETRY_COUNT)
                StartRetryInitializeTimer();
            else
                Logger.LogInfo("Project settings could not be initialized. Rebuild the project to reload settings.");
        }
    }

    private void OnConfigurationChanged(object sender, EventArgs e)
    {
        CheckProjectSettings();
    }

    private void ProjectSystemOnProjectsBuilt(object sender, EventArgs e)
    {
        CheckProjectSettings();
    }

    private void OnSettingsInitialized(ProjectSettings settings)
    {
        Logger.LogInfo($"Project settings initialized: {settings.GetShortLabel()}");
        //SettingsInitialized?.Invoke(this, EventArgs.Empty);
    }

    private ProjectSettings LoadProjectSettings()
    {
        var featureFileCount = _projectScope.GetFeatureFileCount();

        var packageReferences = _projectScope.PackageReferences;
        var isInvalid = packageReferences == null;

        var reqnrollSettings = _reqnrollProjectSettingsProvider.GetReqnrollSettings(packageReferences);
        var hasFeatureFiles = (featureFileCount ?? 0) > 0;
        var kind = GetKind(isInvalid, reqnrollSettings != null, hasFeatureFiles);
        var platformTarget = GetPlatformTarget(_projectScope.PlatformTargetName);

        var targetFrameworkMoniker = TargetFrameworkMoniker.Create(_projectScope.TargetFrameworkMoniker);

        var settings = new ProjectSettings(
            kind,
            targetFrameworkMoniker,
            _projectScope.TargetFrameworkMonikers ?? targetFrameworkMoniker.Value,
            platformTarget,
            _projectScope.OutputAssemblyPath,
            _projectScope.DefaultNamespace,
            reqnrollSettings?.Version,
            reqnrollSettings?.GeneratorFolder,
            reqnrollSettings?.ConfigFilePath,
            reqnrollSettings?.Traits ?? ReqnrollProjectTraits.None,
            GetProgrammingLanguage(_projectScope.ProjectFullName));
        return settings;
    }

    private ProjectPlatformTarget GetPlatformTarget(string platformName)
    {
        if (platformName != null &&
            Enum.TryParse<ProjectPlatformTarget>(platformName.Replace(" ", ""), true, out var platform))
            return platform;

        return ProjectPlatformTarget.Unknown;
    }

    private IdeSupportProjectKind GetKind(bool isInvalid, bool isReqnrollProject, bool hasFeatureFiles)
    {
        if (isInvalid)
            return IdeSupportProjectKind.Uninitialized;

        if (!isReqnrollProject)
            return hasFeatureFiles
                ? IdeSupportProjectKind.FeatureFileContainerProject
                : IdeSupportProjectKind.OtherProject;

        return hasFeatureFiles
            ? IdeSupportProjectKind.ReqnrollTestProject
            : IdeSupportProjectKind.ReqnrollLibProject;
    }

    private static ProjectProgrammingLanguage GetProgrammingLanguage(string projectFullName)
    {
        if (projectFullName.EndsWith(".csproj", StringComparison.InvariantCultureIgnoreCase))
            return ProjectProgrammingLanguage.CSharp;

        if (projectFullName.EndsWith(".vbproj", StringComparison.InvariantCultureIgnoreCase))
            return ProjectProgrammingLanguage.VB;

        if (projectFullName.EndsWith(".fsproj", StringComparison.InvariantCultureIgnoreCase))
            return ProjectProgrammingLanguage.FSharp;

        return ProjectProgrammingLanguage.Other;
    }
}
