#nullable disable
using System;
using System.Xml.Linq;
using System.Collections.Generic;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.Configuration;
using System.Linq;
using System.IO;
using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>ProjectScopeIdeSupportConfigurationProvider</summary>
public class ProjectScopeIdeSupportConfigurationProvider : IIdeSupportConfigurationProvider, IDisposable
{
    /// <summary>File name of the modern Reqnroll JSON configuration file.</summary>
    public const string ReqnrollJsonConfigFileName = ConfigurationFiles.ReqnrollJson;
    /// <summary>File name of the legacy SpecFlow JSON configuration file.</summary>
    public const string SpecFlowJsonConfigFileName = ConfigurationFiles.SpecFlowJson;
    /// <summary>File name of the legacy SpecFlow XML application configuration file.</summary>
    public const string SpecFlowAppConfigFileName = ConfigurationFiles.SpecFlowAppConfig;
    /// <summary>File name of the SpecSync JSON configuration file.</summary>
    public const string SpecSyncJsonConfigFileName = ConfigurationFiles.SpecSyncJson;
    /// <summary>File name of the legacy Deveroom JSON configuration file.</summary>
    public const string IdeSupportConfigFileName = ConfigurationFiles.IdeSupportJson;

    private readonly IProjectScope _projectScope;
    private ConfigCache _configCache;

    /// <summary>Initializes a new instance of the <see cref="ProjectScopeIdeSupportConfigurationProvider"/> class.</summary>
    public ProjectScopeIdeSupportConfigurationProvider(IProjectScope projectScope)
    {
        _projectScope = projectScope ?? throw new ArgumentNullException(nameof(projectScope));
        InitializeConfiguration();
    }

    private IIdeSupportLogger Logger => _projectScope.IdeScope.Logger;
    private ITelemetryService TelemetryService => _projectScope.IdeScope.TelemetryService;
    private IFileSystemForIDE FileSystem => _projectScope.IdeScope.FileSystem;

    /// <summary>Returns the currently cached, resolved configuration for the project.</summary>
    public IdeSupportConfiguration GetConfiguration() => _configCache.Configuration;

    /// <summary>No-op: the provider holds no resources.</summary>
    public void Dispose()
    {
    }

    private void InitializeConfiguration()
    {
        var configSources = _projectScope is VoidProjectScope
            ? Array.Empty<ConfigSource>()
            : GetConfigSources().ToArray();
        _configCache = LoadConfiguration(configSources);
    }

    /// <summary>Re-reads the configuration sources and returns whether the resolved configuration changed.</summary>
    public bool Reload()
    {
        Logger.LogVerbose("Checking configuration...");

        var configSources = GetConfigSources().ToArray();
        if (_configCache.ConfigSources.SequenceEqual(configSources))
            return false; // no source changed

        var oldConfiguration = _configCache.Configuration;
        _configCache = LoadConfiguration(configSources);
        Logger.LogVerbose("Configuration loaded");

        if (oldConfiguration.Equals(_configCache.Configuration))
            return false;

        Logger.LogInfo("Configuration changed");
        return true;
    }

    private IdeSupportConfiguration GetDefaultConfiguration() => new();

    private IEnumerable<ConfigSource> GetConfigSources()
    {
        foreach (var group in ConfigurationFiles.Groups)
        {
            var source = group
                .Select(definition => GetProjectConfigFilePath(definition.FileName))
                .FirstOrDefault(candidate => candidate != null);
            if (source != null)
                yield return source;
        }
    }

    private ConfigSource GetProjectConfigFilePath(string fileName)
    {
        try
        {
            var projectFolder = _projectScope.ProjectFolder;
            var fileSystem = _projectScope.IdeScope.FileSystem;
            var configFilePath = fileSystem.GetFilePathIfExists(Path.Combine(projectFolder, fileName));

            if (fileName.Equals(SpecFlowAppConfigFileName)) configFilePath ??= GetAppConfigPathFromProject();
            if (configFilePath == null)
                return null;

            return ConfigSource.CreateValid(configFilePath, FileSystem.File.GetLastWriteTimeUtc(configFilePath));
        }
        catch (Exception ex)
        {
            Logger.LogDebugException(ex);
            return null;
        }
    }

    private string GetAppConfigPathFromProject()
    {
        var projectFilePath = _projectScope.ProjectFullName;
        using var configFile = FileSystem.File.OpenRead(_projectScope.ProjectFullName);
        XElement csProjXElement = XElement.Load(configFile);

        string appConfigPath = csProjXElement
            .Element("PropertyGroup")?
            .Element("AppConfig")?
            .Value;
        if (!string.IsNullOrEmpty(appConfigPath) && !Path.IsPathRooted(appConfigPath))
            appConfigPath = Path.Combine(Path.GetDirectoryName(projectFilePath)!, appConfigPath);

        return appConfigPath;
    }

    private ConfigCache LoadConfiguration(ConfigSource[] configSources)
    {
        var loadedSources = new List<ConfigSource>();
        var configuration = GetDefaultConfiguration();

        foreach (var configSource in configSources)
            try
            {
                // Each source is applied to a copy and committed only once it has loaded and validated, so a
                // failing source can neither leave partial changes behind nor invalidate the other sources.
                var candidate = CloneConfiguration(configuration);
                if (ConfigurationFiles.TryGetDefinition(configSource.FilePath, out var definition))
                {
                    Logger.LogVerbose($"Loading configuration from '{configSource.FilePath}'");
                    new ConfigurationFileLoader(FileSystem).Update(candidate, definition, configSource.FilePath);
                }

                candidate.CheckConfiguration();

                configuration = candidate;
                loadedSources.Add(configSource);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unable to load configuration from '{configSource.FilePath}': {ex.Message}");
                Logger.LogVerboseException(TelemetryService, ex, "Unable to load configuration");
            }

        // 'configuration' is always the default or a candidate that passed CheckConfiguration(), so no final
        // validation (or fall-back to defaults) is needed.
        if (loadedSources.Any())
            configuration.ConfigurationChangeTime = loadedSources.Max(cs => cs.LastChangeTime);

        return new ConfigCache(configuration, loadedSources.ToArray());
    }

    // All configuration classes are plain property bags (the same ones the JSON loaders populate), so a JSON
    // round trip is a faithful deep copy; nulls are kept so that an explicitly null value survives the copy.
    private static IdeSupportConfiguration CloneConfiguration(IdeSupportConfiguration configuration) =>
        JsonConvert.DeserializeObject<IdeSupportConfiguration>(JsonConvert.SerializeObject(configuration));

    /// <summary>Returns a string identifying this provider and the config sources it loaded from.</summary>
    public override string ToString() =>
        $"{nameof(ProjectScopeIdeSupportConfigurationProvider)}({string.Join(",", _configCache.ConfigSources.Select(cs => cs.ToString()))})";
}
