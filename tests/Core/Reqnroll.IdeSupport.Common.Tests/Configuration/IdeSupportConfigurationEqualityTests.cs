using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.Configuration.Loading;
using Reqnroll.IdeSupport.Common.Configuration.Model;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

/// <summary>
/// Configuration equality is how a reload decides whether anything changed, so two configurations
/// loaded from the same content must be equal and a change to any loaded setting must make them differ.
/// </summary>
public class IdeSupportConfigurationEqualityTests
{
    private static IdeSupportConfiguration Load(string json)
    {
        var configuration = new IdeSupportConfiguration();
        new ReqnrollConfigDeserializer().Populate(json, configuration);
        configuration.CheckConfiguration();
        return configuration;
    }

    private const string FullConfiguration = """
        {
          "language": { "feature": "fr", "binding": "de-DE" },
          "trace": { "stepDefinitionSkeletonStyle": "RegexAttribute" },
          "ide": {
            "traceability": { "tagLinks": [ { "tagPattern": "ISSUE-(?<id>\\d+)", "urlTemplate": "https://x/{id}" } ] },
            "editor": { "gherkinFormat": { "indentSteps": false } },
            "bindingDiscovery": { "connectorPath": "connector.exe" },
            "reqnroll": { "traits": [ "XUnitAdapter" ] }
          }
        }
        """;

    [Fact]
    public void Default_configurations_are_equal()
    {
        var first = new IdeSupportConfiguration();
        var second = new IdeSupportConfiguration();

        second.Should().Be(first);
        second.GetHashCode().Should().Be(first.GetHashCode());
    }

    [Fact]
    public void Configurations_loaded_from_the_same_content_are_equal()
    {
        var first = Load(FullConfiguration);
        var second = Load(FullConfiguration);

        second.Should().Be(first);
        second.GetHashCode().Should().Be(first.GetHashCode());
    }

    [Theory]
    [InlineData("""{ "language": { "feature": "de" } }""")]
    [InlineData("""{ "language": { "binding": "de-DE" } }""")]
    [InlineData("""{ "trace": { "stepDefinitionSkeletonStyle": "RegexAttribute" } }""")]
    [InlineData("""{ "trace": { "stepDefinitionSkeletonStyle": "AsyncCucumberExpressionAttribute" } }""")]
    [InlineData("""{ "ide": { "traceability": { "tagLinks": [ { "tagPattern": "a", "urlTemplate": "u" } ] } } }""")]
    [InlineData("""{ "ide": { "editor": { "gherkinFormat": { "indentSteps": false } } } }""")]
    [InlineData("""{ "ide": { "editor": { "showStepCompletionAfterStepKeywords": false } } }""")]
    [InlineData("""{ "ide": { "bindingDiscovery": { "connectorPath": "connector.exe" } } }""")]
    [InlineData("""{ "ide": { "debugConnector": true } }""")]
    [InlineData("""{ "ide": { "reqnroll": { "traits": [ "XUnitAdapter" ] } } }""")]
    [InlineData("""{ "ide": { "reqnroll": { "version": "2.0.0" } } }""")]
    public void A_change_to_any_loaded_setting_makes_the_configurations_differ(string changedContent)
    {
        var baseline = new IdeSupportConfiguration();
        baseline.CheckConfiguration();

        Load(changedContent).Should().NotBe(baseline);
    }

    [Fact]
    public void Changing_only_one_tag_link_makes_the_configurations_differ()
    {
        var first = Load("""{ "ide": { "traceability": { "tagLinks": [ { "tagPattern": "a", "urlTemplate": "u" } ] } } }""");
        var second = Load("""{ "ide": { "traceability": { "tagLinks": [ { "tagPattern": "a", "urlTemplate": "v" } ] } } }""");

        second.Should().NotBe(first);
    }
}
