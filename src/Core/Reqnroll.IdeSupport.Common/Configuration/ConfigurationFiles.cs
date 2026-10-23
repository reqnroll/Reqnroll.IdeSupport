#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>How the content of a configuration file is read into an <see cref="IdeSupportConfiguration"/>.</summary>
public enum ConfigurationFileFormat
{
    /// <summary>A <c>reqnroll.json</c>-style file (<c>reqnroll.json</c>, <c>specflow.json</c>).</summary>
    ReqnrollJson,
    /// <summary>A SpecFlow XML application configuration file.</summary>
    SpecFlowXml,
    /// <summary>A <c>specsync.json</c> file.</summary>
    SpecSyncJson,
    /// <summary>The legacy Deveroom <c>deveroom.json</c> file.</summary>
    IdeSupportJson
}

/// <summary>A configuration file name and the format used to read it.</summary>
public sealed record ConfigurationFileDefinition(string FileName, ConfigurationFileFormat Format);

/// <summary>
/// The configuration files the IDE support reads: their names, the format of each, and the order in which
/// they are applied. This is the one place that knows which files exist, so loading and file watching cannot
/// drift apart.
/// </summary>
public static class ConfigurationFiles
{
    /// <summary>File name of the modern Reqnroll JSON configuration file.</summary>
    public const string ReqnrollJson = "reqnroll.json";
    /// <summary>File name of the legacy SpecFlow JSON configuration file.</summary>
    public const string SpecFlowJson = "specflow.json";
    /// <summary>File name of the legacy SpecFlow XML application configuration file.</summary>
    public const string SpecFlowAppConfig = "App.config";
    /// <summary>File name of the SpecSync JSON configuration file.</summary>
    public const string SpecSyncJson = "specsync.json";
    /// <summary>File name of the legacy Deveroom JSON configuration file.</summary>
    public const string IdeSupportJson = "deveroom.json";

    /// <summary>
    /// The groups applied in order. Within a group only the first file that exists is used (the primary
    /// Reqnroll/SpecFlow settings come from exactly one file); every later group adds its settings on top.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ConfigurationFileDefinition>> Groups { get; } = new[]
    {
        new[]
        {
            new ConfigurationFileDefinition(ReqnrollJson, ConfigurationFileFormat.ReqnrollJson),
            new ConfigurationFileDefinition(SpecFlowJson, ConfigurationFileFormat.ReqnrollJson),
            new ConfigurationFileDefinition(SpecFlowAppConfig, ConfigurationFileFormat.SpecFlowXml)
        },
        new[] { new ConfigurationFileDefinition(SpecSyncJson, ConfigurationFileFormat.SpecSyncJson) },
        new[] { new ConfigurationFileDefinition(IdeSupportJson, ConfigurationFileFormat.IdeSupportJson) }
    };

    /// <summary>The file names to watch for changes: every file any group can read.</summary>
    public static IReadOnlyList<string> WatchedFileNames { get; } =
        Groups.SelectMany(g => g).Select(d => d.FileName).ToArray();

    /// <summary>Finds the definition for a file name or path (case-insensitive on the file name).</summary>
    public static bool TryGetDefinition(string fileNameOrPath, out ConfigurationFileDefinition definition)
    {
        var fileName = Path.GetFileName(fileNameOrPath);
        definition = Groups.SelectMany(g => g)
            .FirstOrDefault(d => d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }

    /// <summary>Whether <paramref name="fileNameOrPath"/> names a configuration file that is read.</summary>
    public static bool IsConfigurationFile(string fileNameOrPath) => TryGetDefinition(fileNameOrPath, out _);

    /// <summary>
    /// Returns the path of the primary Reqnroll/SpecFlow configuration file in <paramref name="folder"/>
    /// (the first existing file of the first group), or <see langword="null"/> if there is none.
    /// </summary>
    public static string FindPrimaryConfigFile(IFileSystemForIDE fileSystem, string folder)
    {
        foreach (var definition in Groups[0])
        {
            var path = fileSystem.GetFilePathIfExists(Path.Combine(folder, definition.FileName));
            if (path != null)
                return path;
        }

        return null;
    }
}
