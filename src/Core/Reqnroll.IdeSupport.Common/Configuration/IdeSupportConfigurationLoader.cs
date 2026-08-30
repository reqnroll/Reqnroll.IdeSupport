#nullable disable

using System;
using System.IO;
using System.Linq.Expressions;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>IdeSupportConfigurationLoader</summary>
public class IdeSupportConfigurationLoader
{
    private readonly IConfigDeserializer<IdeSupportConfiguration> _configDeserializer;
    private readonly IFileSystemForIDE _fileSystem;

    private IdeSupportConfigurationLoader(
        IConfigDeserializer<IdeSupportConfiguration> configDeserializer,
        IFileSystemForIDE fileSystem)
    {
        _configDeserializer = configDeserializer;
        _fileSystem = fileSystem;
    }

    /// <summary>Creates a loader that reads settings from a <c>reqnroll.json</c> file.</summary>
    public static IdeSupportConfigurationLoader CreateReqnrollJsonConfigurationLoader(IFileSystemForIDE fileSystem) =>
        new(new ReqnrollConfigDeserializer(), fileSystem);

    /// <summary>Creates a loader that reads settings from a legacy Deveroom-style JSON configuration file.</summary>
    public static IdeSupportConfigurationLoader CreateIdeSupportJsonConfigurationLoader(IFileSystemForIDE fileSystem) =>
        new(new JsonNetConfigDeserializer<IdeSupportConfiguration>(), fileSystem);

    /// <summary>Loads a new <see cref="IdeSupportConfiguration"/> from the given config file.</summary>
    public IdeSupportConfiguration Load(string configFilePath)
    {
        var config = new IdeSupportConfiguration();
        Update(config, configFilePath);
        return config;
    }

    /// <summary>Re-reads the given config file and applies its settings onto the existing <paramref name="config"/> instance.</summary>
    public void Update(IdeSupportConfiguration config, string configFilePath)
    {
        if (!_fileSystem.File.Exists(configFilePath))
            throw new IdeSupportConfigurationException($"The specified config file '{configFilePath}' does not exist.");
        var configFolder = Path.GetDirectoryName(configFilePath) ??
                           throw new IdeSupportConfigurationException(
                               $"The specified config file '{configFilePath}' does not contain a folder.");

        var jsonString = _fileSystem.File.ReadAllText(configFilePath);
        Update(config, jsonString, configFolder);
    }

    private void Update(IdeSupportConfiguration config, string configFileContent, string configFolder)
    {
        _configDeserializer.Populate(configFileContent, config);

        config.ConfigurationBaseFolder = configFolder;

        config.Reqnroll.ConfigFilePath = EnsureFullPath(config, c => c.Reqnroll.ConfigFilePath);
        //config.SpecFlow.ConfigFilePath = EnsureFullPath(config, c => c.SpecFlow.ConfigFilePath);
        //config.SpecFlow.GeneratorFolder = EnsureFullPath(config, c => c.SpecFlow.GeneratorFolder, true);
    }

    private string ExpandEnvironmentVariables(string value)
    {
        if (value == null)
            return null;
        return Environment.ExpandEnvironmentVariables(value);
    }

    private string EnsureFullPath(IdeSupportConfiguration config, string filePath, string label, bool isFolder = false)
    {
        if (filePath == null)
            return null;
        filePath = ExpandEnvironmentVariables(filePath);
        var fullPath = Path.GetFullPath(Path.Combine(config.ConfigurationBaseFolder, filePath));
        if (!isFolder && !_fileSystem.File.Exists(fullPath))
            throw new IdeSupportConfigurationException(
                $"Unable to access file '{fullPath}'. Please make sure you specify a path for an existing file for the {label} option.");
        if (isFolder && !_fileSystem.Directory.Exists(fullPath))
            throw new IdeSupportConfigurationException(
                $"Unable to access directory '{fullPath}'. Please make sure you specify a path for an existing directory for the {label} option.");
        return fullPath;
    }

    private string EnsureFullPath(IdeSupportConfiguration config,
        Expression<Func<IdeSupportConfiguration, string>> configAccessor, bool isFolder = false)
    {
        var filePath = configAccessor.Compile().Invoke(config);
        return EnsureFullPath(config, filePath, configAccessor.ToString(), isFolder);
    }
}
