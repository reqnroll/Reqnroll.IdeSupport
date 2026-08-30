using AwesomeAssertions;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class ReqnrollConfigDeserializerTests
{
    [Fact]
    public void Should_parse_language_feature_and_binding_from_reqnroll_json()
    {
        // Arrange
        var deserializer = new ReqnrollConfigDeserializer();
        var config = new IdeSupportConfiguration();
        var json = """
        {
          "language": {
            "feature": "en-US",
            "binding": "fr-FR"
          }
        }
        """;

        // Act
        deserializer.Populate(json, config);

        // Assert
        config.DefaultFeatureLanguage.Should().Be("en-US");
        config.ConfiguredBindingCulture.Should().Be("fr-FR");
        config.BindingCulture.Should().Be("fr-FR");
    }

    [Fact]
    public void Should_keep_defaults_when_no_language_configuration()
    {
        // Arrange
        var deserializer = new ReqnrollConfigDeserializer();
        var config = new IdeSupportConfiguration();
        var json = """
        {
          "trace": {
            "stepDefinitionSkeletonStyle": "CucumberExpressionAttribute"
          }
        }
        """;

        // Act
        deserializer.Populate(json, config);

        // Assert
        config.DefaultFeatureLanguage.Should().Be("en-US"); // Default
        config.ConfiguredBindingCulture.Should().BeNull(); // Default
        config.BindingCulture.Should().Be("en-US"); // Falls back to feature language
    }

    [Fact]
    public void Should_support_legacy_specflow_binding_culture_format()
    {
        // Arrange
        var deserializer = new ReqnrollConfigDeserializer();
        var config = new IdeSupportConfiguration();
        var json = """
        {
          "bindingCulture": {
            "name": "de-DE"
          }
        }
        """;

        // Act
        deserializer.Populate(json, config);

        // Assert
        config.DefaultFeatureLanguage.Should().Be("en-US"); // Default
        config.ConfiguredBindingCulture.Should().Be("de-DE");
        config.BindingCulture.Should().Be("de-DE");
    }

    [Fact]
    public void Should_prioritize_language_binding_over_legacy_bindingCulture()
    {
        // Arrange
        var deserializer = new ReqnrollConfigDeserializer();
        var config = new IdeSupportConfiguration();
        var json = """
        {
          "language": {
            "binding": "fr-FR"
          },
          "bindingCulture": {
            "name": "de-DE"
          }
        }
        """;

        // Act
        deserializer.Populate(json, config);

        // Assert
        config.DefaultFeatureLanguage.Should().Be("en-US"); // Default
        config.ConfiguredBindingCulture.Should().Be("fr-FR"); // language.binding takes priority
        config.BindingCulture.Should().Be("fr-FR");
    }

    [Theory]
    [InlineData("RegexAttribute", SnippetExpressionStyle.RegularExpression)]
    [InlineData("CucumberExpressionAttribute", SnippetExpressionStyle.CucumberExpression)]
    [InlineData("AsyncRegexAttribute", SnippetExpressionStyle.AsyncRegularExpression)]
    [InlineData("AsyncCucumberExpressionAttribute", SnippetExpressionStyle.AsyncCucumberExpression)]
    [InlineData("InvalidValue", SnippetExpressionStyle.CucumberExpression)] // Default fallback
    [InlineData("", SnippetExpressionStyle.CucumberExpression)] // Default fallback
    [InlineData(null, SnippetExpressionStyle.CucumberExpression)] // Default fallback
    public void Should_set_stepDefinitionSkeletonStyle_from_reqnroll_json(string? styleValue, SnippetExpressionStyle expectedStyle)
    {
        // Arrange
        var deserializer = new ReqnrollConfigDeserializer();
        var config = new IdeSupportConfiguration();
        var styleJson = styleValue != null
            ? $@"
            {{
              ""trace"": {{
                ""stepDefinitionSkeletonStyle"": ""{styleValue}""
              }}
            }}"
            : @"
            {
              ""trace"": {
              }
            }";

        // Act
        deserializer.Populate(styleJson, config);

        // Assert
        config.SnippetExpressionStyle.Should().Be(expectedStyle);
    }
}
