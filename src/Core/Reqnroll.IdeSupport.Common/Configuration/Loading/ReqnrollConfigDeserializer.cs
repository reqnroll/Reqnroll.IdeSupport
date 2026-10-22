#nullable disable

using Reqnroll.IdeSupport.Common.Configuration;
using System.Collections.Generic;

namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>ReqnrollConfigDeserializer</summary>
public class ReqnrollConfigDeserializer : IConfigDeserializer<IdeSupportConfiguration>
{
    private readonly JsonNetConfigDeserializer<ReqnrollJsonConfiguration> _reqnrollConfigDeserializer = new();

    /// <summary>Parses the given <c>reqnroll.json</c> content and applies its settings onto <paramref name="config"/>.</summary>
    public void Populate(string jsonString, IdeSupportConfiguration config)
    {
        var reqnrollJsonConfiguration = new ReqnrollJsonConfiguration {Ide = config};
        _reqnrollConfigDeserializer.Populate(jsonString, reqnrollJsonConfiguration);
        if (reqnrollJsonConfiguration.Language != null &&
            reqnrollJsonConfiguration.Language.TryGetValue("feature", out var featureLanguage))
            config.DefaultFeatureLanguage = featureLanguage;
        if (reqnrollJsonConfiguration.BindingCulture != null &&
            reqnrollJsonConfiguration.BindingCulture.TryGetValue("name", out var bindingCultureFromSpecFlow))
            config.ConfiguredBindingCulture = bindingCultureFromSpecFlow;
        if (reqnrollJsonConfiguration.Language != null &&
            reqnrollJsonConfiguration.Language.TryGetValue("binding", out var bindingCulture))
            config.ConfiguredBindingCulture = bindingCulture;
        if (reqnrollJsonConfiguration.Trace != null &&
            reqnrollJsonConfiguration.Trace.TryGetValue("stepDefinitionSkeletonStyle", out var sdSnippetStyle)) {
            config.SnippetExpressionStyle = sdSnippetStyle switch
            {
                "CucumberExpressionAttribute" => SnippetExpressionStyle.CucumberExpression,
                "RegexAttribute" => SnippetExpressionStyle.RegularExpression,
                "AsyncCucumberExpressionAttribute" => SnippetExpressionStyle.AsyncCucumberExpression,
                "AsyncRegexAttribute" => SnippetExpressionStyle.AsyncRegularExpression,
                _ => SnippetExpressionStyle.CucumberExpression
            };
        }
    }

    private class ReqnrollJsonConfiguration
    {
        public IdeSupportConfiguration Ide { get; set; }
        public Dictionary<string, string> Language { get; set; }
        public Dictionary<string, string> BindingCulture { get; set; }
        public Dictionary<string, string> Trace { get; set; }
    }
}
