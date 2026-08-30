#nullable disable
using System;
using System.Xml.XPath;
using System.Xml.Linq;
using System.Collections.Generic;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.Common.Configuration;
using System.Linq;
using System.IO;
using System.Collections;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>ProjectScopeIdeSupportConfigurationProvider</summary>
public class ProjectScopeIdeSupportConfigurationProvider : IIdeSupportConfigurationProvider, IDisposable
{
    /// <summary>File name of the modern Reqnroll JSON configuration file.</summary>
    public const string ReqnrollJsonConfigFileName = "reqnroll.json";
    /// <summary>File name of the legacy SpecFlow JSON configuration file.</summary>
    public const string SpecFlowJsonConfigFileName = "specflow.json";
    /// <summary>File name of the legacy SpecFlow XML application configuration file.</summary>
    public const string SpecFlowAppConfigFileName = "App.config";
    /// <summary>File name of the SpecSync JSON configuration file.</summary>
    public const string SpecSyncJsonConfigFileName = "specsync.json";
    /// <summary>File name of the legacy Deveroom JSON configuration file.</summary>
    public const string IdeSupportConfigFileName = "deveroom.json";

    private readonly IProjectScope _projectScope;
    private ConfigCache _configCache;

    /// <summary>Initializes a new instance of the <see cref="ProjectScopeIdeSupportConfigurationProvider"/> class.</summary>
    public ProjectScopeIdeSupportConfigurationProvider(IProjectScope projectScope)
    {
        _projectScope = projectScope ?? throw new ArgumentNullException(nameof(projectScope));
        InitializeConfiguration();

        //_projectScope.IdeScope.WeakProjectsBuilt += ProjectSystemOnProjectsBuilt;
    }

    private IIdeSupportLogger Logger => _projectScope.IdeScope.Logger;
    private ITelemetryService TelemetryService => _projectScope.IdeScope.TelemetryService;
    private IFileSystemForIDE FileSystem => _projectScope.IdeScope.FileSystem;

    //public event EventHandler<EventArgs> WeakConfigurationChanged
    //{
    //    add => WeakEventManager<ProjectScopeIdeSupportConfigurationProvider, EventArgs>.AddHandler(this,
    //        nameof(ConfigurationChanged), value);
    //    remove => WeakEventManager<ProjectScopeIdeSupportConfigurationProvider, EventArgs>.RemoveHandler(this,
    //        nameof(ConfigurationChanged), value);
    //}

    /// <summary>Returns the currently cached, resolved configuration for the project.</summary>
    public IdeSupportConfiguration GetConfiguration() => _configCache.Configuration;

    /// <summary>No-op: reserved for unsubscribing project-build event handlers if that wiring is re-enabled.</summary>
    public void Dispose()
    {
        //_projectScope.IdeScope.WeakProjectsBuilt -= ProjectSystemOnProjectsBuilt;
    }

    /// <summary>Raised after the configuration is reloaded and found to have changed.</summary>
    public event EventHandler ConfigurationChanged;

    private void InitializeConfiguration()
    {
        var configSources = _projectScope is VoidProjectScope
            ? Array.Empty<ConfigSource>()
            : GetConfigSources().ToArray();
        _configCache = LoadConfiguration(configSources);
    }

    private void ProjectSystemOnProjectsBuilt(object sender, EventArgs e)
    {
        CheckConfiguration(true);
    }

    /// <summary>Forces a re-check of configuration sources, raising <see cref="ConfigurationChanged"/> if anything changed.</summary>
    public void Reload() => CheckConfiguration(triggerChanged: true);

    private void CheckConfiguration(bool triggerChanged)
    {
        Logger.LogVerbose("Checking configuration...");

        var configSources = GetConfigSources().ToArray();
        if (_configCache.ConfigSources.SequenceEqual(configSources))
            return; // no source changed

        var oldConfiguration = _configCache.Configuration;
        _configCache = LoadConfiguration(configSources);
        Logger.LogVerbose("Configuration loaded");

        if (triggerChanged && !oldConfiguration.Equals(_configCache.Configuration))
        {
            Logger.LogInfo("Configuration changed");
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private IdeSupportConfiguration GetDefaultConfiguration() => new();

    private IEnumerable<ConfigSource> GetConfigSources()
    {
        var jsonSource = GetProjectConfigFilePath(ReqnrollJsonConfigFileName);
        if (jsonSource != null)
        {
            yield return jsonSource;
        }
        else
        {
            var specFlowJsonSource = GetProjectConfigFilePath(SpecFlowJsonConfigFileName);
            if (specFlowJsonSource != null)
            {
                yield return specFlowJsonSource;
            }
            else
            {
                var appConfigSource = GetProjectConfigFilePath(SpecFlowAppConfigFileName);
                if (appConfigSource != null)
                    yield return appConfigSource;
            }
        }

        var specSyncConfigSource = GetProjectConfigFilePath(SpecSyncJsonConfigFileName);
        if (specSyncConfigSource != null)
            yield return specSyncConfigSource;

        var deveroomConfigSource = GetProjectConfigFilePath(IdeSupportConfigFileName);
        if (deveroomConfigSource != null)
            yield return deveroomConfigSource;
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
                var fileName = Path.GetFileName(configSource.FilePath);
                if (ReqnrollJsonConfigFileName.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                    LoadFromReqnrollJsonConfig(configSource.FilePath, configuration);

                if (SpecFlowAppConfigFileName.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                    LoadFromSpecFlowXmlConfig(configSource.FilePath, configuration);

                if (SpecFlowJsonConfigFileName.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                    LoadFromSpecFlowJsonConfig(configSource.FilePath, configuration);

                if (SpecSyncJsonConfigFileName.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                    LoadFromSpecSyncJsonConfig(configSource.FilePath, configuration);

                if (IdeSupportConfigFileName.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                    LoadFromIdeSupportConfig(configSource.FilePath, configuration);

                configuration.CheckConfiguration();

                loadedSources.Add(configSource);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unable to load configuration from '{configSource.FilePath}': {ex.Message}");
                Logger.LogVerboseException(TelemetryService, ex, "Unable to load configuration");
            }

        if (loadedSources.Any())
            configuration.ConfigurationChangeTime = loadedSources.Max(cs => cs.LastChangeTime);

        try
        {
            configuration.CheckConfiguration();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Invalid Reqnroll Visual Studio configuration: {ex.Message}");
            Logger.LogVerboseException(TelemetryService, ex, "Configuration error, using default config");
            configuration = new IdeSupportConfiguration();
        }

        return new ConfigCache(configuration, loadedSources.ToArray());
    }

    private void LoadFromIdeSupportConfig(string configSourceFilePath, IdeSupportConfiguration configuration)
    {
        Logger.LogVerbose($"Loading Deveroom config from '{configSourceFilePath}'");
        var loader = IdeSupportConfigurationLoader.CreateIdeSupportJsonConfigurationLoader(FileSystem);
        loader.Update(configuration, configSourceFilePath);
    }

    private string XPathEvaluateAttribute(XDocument doc, string xpath) => (doc.XPathEvaluate(xpath) as IEnumerable)
        ?.OfType<XAttribute>().FirstOrDefault()?.Value;

    private void LoadFromSpecFlowXmlConfig(string configSourceFilePath, IdeSupportConfiguration configuration)
    {
        var fileContent = FileSystem.File.ReadAllText(configSourceFilePath);
        var configDoc = XDocument.Parse(fileContent);
        var featureLang = XPathEvaluateAttribute(configDoc, "/configuration/specFlow/language/@feature");
        if (featureLang != null)
            configuration.DefaultFeatureLanguage = featureLang;
        var bindingCulture = XPathEvaluateAttribute(configDoc, "/configuration/specFlow/bindingCulture/@name");
        if (bindingCulture != null)
            configuration.ConfiguredBindingCulture = bindingCulture;
    }

    private void LoadFromReqnrollJsonConfig(string configSourceFilePath, IdeSupportConfiguration configuration)
    {
        Logger.LogVerbose($"Loading configuration from '{configSourceFilePath}'");
        var configLoader = IdeSupportConfigurationLoader.CreateReqnrollJsonConfigurationLoader(FileSystem);
        configLoader.Update(configuration, configSourceFilePath);
    }

    private void LoadFromSpecFlowJsonConfig(string configSourceFilePath, IdeSupportConfiguration configuration)
    {
        LoadFromReqnrollJsonConfig(configSourceFilePath, configuration);
    }

    private void LoadFromSpecSyncJsonConfig(string configSourceFilePath, IdeSupportConfiguration configuration)
    {
        var fileContent = FileSystem.File.ReadAllText(configSourceFilePath);
        UpdateFromSpecSyncJsonConfig(configuration, fileContent);
    }

    internal static void UpdateFromSpecSyncJsonConfig(IdeSupportConfiguration configuration, string fileContent)
    {
        var configDoc = JObject.Parse(fileContent);

        var projectUrl = ((string) configDoc["remote"]?["projectUrl"])?.TrimEnd('/');
        if (string.IsNullOrEmpty(projectUrl))
            return;

        var testCaseTagPrefix = (string) configDoc["synchronization"]?["testCaseTagPrefix"] ?? "tc";

        var tagLinks = new List<TagLinkConfiguration>(configuration.Traceability.TagLinks);
        AddSpecSyncTagLinkConfiguration(tagLinks, testCaseTagPrefix, projectUrl);

        var linksArray = configDoc["synchronization"]?["links"] as JArray;
        if (linksArray != null)
            foreach (var link in linksArray)
            {
                var tagPrefix = (string) link["tagPrefix"];
                if (string.IsNullOrEmpty(tagPrefix))
                    continue;
                AddSpecSyncTagLinkConfiguration(tagLinks, tagPrefix, projectUrl);
            }

        configuration.Traceability.TagLinks = tagLinks.ToArray();
    }

    private static void AddSpecSyncTagLinkConfiguration(List<TagLinkConfiguration> tagLinks, string tagPrefix,
        string projectUrl)
    {
        tagLinks.Add(new TagLinkConfiguration
        {
            TagPattern = $@"{tagPrefix}\:(?<id>\d+)",
            UrlTemplate = projectUrl + "/_workitems/edit/{id}"
        });
    }

    /// <summary>Returns a string identifying this provider and the config sources it loaded from.</summary>
    public override string ToString() =>
        $"{nameof(ProjectScopeIdeSupportConfigurationProvider)}({string.Join(",", _configCache.ConfigSources.Select(cs => cs.ToString()))})";
}
