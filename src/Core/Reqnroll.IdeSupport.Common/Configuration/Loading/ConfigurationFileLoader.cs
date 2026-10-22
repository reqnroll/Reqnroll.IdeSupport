#nullable disable

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>Reads a configuration file of any supported <see cref="ConfigurationFileFormat"/> onto an <see cref="IdeSupportConfiguration"/>.</summary>
public sealed class ConfigurationFileLoader
{
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="ConfigurationFileLoader"/> class.</summary>
    public ConfigurationFileLoader(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>Applies the settings in the file at <paramref name="filePath"/> onto <paramref name="configuration"/>.</summary>
    public void Update(IdeSupportConfiguration configuration, ConfigurationFileDefinition definition, string filePath)
    {
        switch (definition.Format)
        {
            case ConfigurationFileFormat.ReqnrollJson:
                IdeSupportConfigurationLoader.CreateReqnrollJsonConfigurationLoader(_fileSystem)
                    .Update(configuration, filePath);
                break;
            case ConfigurationFileFormat.IdeSupportJson:
                IdeSupportConfigurationLoader.CreateIdeSupportJsonConfigurationLoader(_fileSystem)
                    .Update(configuration, filePath);
                break;
            case ConfigurationFileFormat.SpecFlowXml:
                UpdateFromSpecFlowXml(configuration, _fileSystem.File.ReadAllText(filePath));
                break;
            case ConfigurationFileFormat.SpecSyncJson:
                UpdateFromSpecSyncJson(configuration, _fileSystem.File.ReadAllText(filePath));
                break;
        }
    }

    internal static void UpdateFromSpecFlowXml(IdeSupportConfiguration configuration, string fileContent)
    {
        var configDoc = XDocument.Parse(fileContent);
        var featureLang = XPathEvaluateAttribute(configDoc, "/configuration/specFlow/language/@feature");
        if (featureLang != null)
            configuration.DefaultFeatureLanguage = featureLang;
        var bindingCulture = XPathEvaluateAttribute(configDoc, "/configuration/specFlow/bindingCulture/@name");
        if (bindingCulture != null)
            configuration.ConfiguredBindingCulture = bindingCulture;
    }

    private static string XPathEvaluateAttribute(XDocument doc, string xpath) => (doc.XPathEvaluate(xpath) as IEnumerable)
        ?.OfType<XAttribute>().FirstOrDefault()?.Value;

    internal static void UpdateFromSpecSyncJson(IdeSupportConfiguration configuration, string fileContent)
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
            TagPattern = $@"{Regex.Escape(tagPrefix)}\:(?<id>\d+)",
            UrlTemplate = projectUrl + "/_workitems/edit/{id}"
        });
    }
}
