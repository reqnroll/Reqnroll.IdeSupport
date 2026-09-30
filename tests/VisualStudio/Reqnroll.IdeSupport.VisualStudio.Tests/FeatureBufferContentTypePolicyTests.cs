using System;
using System.Linq;
using AwesomeAssertions;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;
using NSubstitute;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests;

/// <summary>
/// Covers the rule <see cref="FeatureBufferContentTypeGuard"/> applies to decide whether a
/// <c>.feature</c> buffer is re-typed to <c>Gherkin</c> (issue #78).
/// </summary>
public class FeatureBufferContentTypePolicyTests
{
    private const string Feature = @"C:\repo\Features\Login.feature";
    private const string Steps = @"C:\repo\StepDefinitions\LoginSteps.cs";

    private static readonly IContentType Any = ContentType("any");
    private static readonly IContentType Text = ContentType(StandardContentTypeNames.Text, Any);
    private static readonly IContentType Code = ContentType(StandardContentTypeNames.Code, Text);
    private static readonly IContentType LanguageServerBase = ContentType(CodeRemoteContentDefinition.CodeRemoteContentTypeName, Code);
    private static readonly IContentType Gherkin = ContentType(VsWellKnownIds.GherkinContentType, LanguageServerBase);
    private static readonly IContentType Inert = ContentType("inert");
    private static readonly IContentType Projection = ContentType("projection", Any);

    /// <summary>A content type stub whose <c>IsOfType</c> walks its bases, as VS's does (case-insensitively).</summary>
    private static IContentType ContentType(string name, params IContentType[] bases)
    {
        var contentType = Substitute.For<IContentType>();
        contentType.TypeName.Returns(name);
        contentType.BaseTypes.Returns(bases);
        contentType.IsOfType(Arg.Any<string>()).Returns(call =>
        {
            var type = call.Arg<string>();
            return string.Equals(name, type, StringComparison.OrdinalIgnoreCase) || bases.Any(b => b.IsOfType(type));
        });
        return contentType;
    }

    [Fact]
    public void Retypes_a_feature_file_in_a_plain_text_buffer()
    {
        FeatureBufferContentTypePolicy.Decide(Feature, Text, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.Retype);
    }

    [Fact]
    public void Retypes_a_feature_file_in_a_code_buffer_of_another_language()
    {
        FeatureBufferContentTypePolicy.Decide(Feature, Code, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.Retype);
    }

    [Fact]
    public void Leaves_a_buffer_that_is_already_Gherkin_alone()
    {
        FeatureBufferContentTypePolicy.Decide(Feature, Gherkin, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.AlreadyGherkin);
    }

    [Fact]
    public void Treats_a_type_derived_from_Gherkin_as_already_Gherkin()
    {
        var derived = ContentType("Gherkin-Custom", Gherkin);

        FeatureBufferContentTypePolicy.Decide(Feature, derived, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.AlreadyGherkin);
    }

    [Theory]
    [InlineData(Steps)]
    [InlineData(@"C:\repo\README.md")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_documents_that_are_not_feature_files(string? path)
    {
        FeatureBufferContentTypePolicy.Decide(path, Text, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.NotAFeatureFile);
    }

    [Fact]
    public void Matches_the_feature_extension_case_insensitively()
    {
        FeatureBufferContentTypePolicy.Decide(@"C:\repo\Features\LOGIN.FEATURE", Text, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.Retype);
    }

    [Fact]
    public void Reports_when_the_Gherkin_content_type_is_not_registered()
    {
        FeatureBufferContentTypePolicy.Decide(Feature, Text, null)
            .Should().Be(FeatureBufferContentTypeAction.GherkinUnavailable);
    }

    [Fact]
    public void An_already_Gherkin_buffer_needs_nothing_even_when_the_registry_lookup_fails()
    {
        FeatureBufferContentTypePolicy.Decide(Feature, Gherkin, null)
            .Should().Be(FeatureBufferContentTypeAction.AlreadyGherkin);
    }

    [Theory]
    [MemberData(nameof(NonTextBuffers))]
    public void Leaves_buffers_that_are_not_plain_text_alone(IContentType current)
    {
        FeatureBufferContentTypePolicy.Decide(Feature, current, Gherkin)
            .Should().Be(FeatureBufferContentTypeAction.NotATextBuffer);
    }

    public static TheoryData<IContentType> NonTextBuffers() => new() { Inert, Projection };

    [Fact]
    public void Describes_a_content_type_with_its_direct_bases()
    {
        FeatureBufferContentTypePolicy.Describe(Code).Should().Be("'code' (bases: text)");
    }

    [Fact]
    public void Describes_a_content_type_without_bases()
    {
        FeatureBufferContentTypePolicy.Describe(Inert).Should().Be("'inert' (no bases)");
    }

    [Fact]
    public void Describes_a_missing_content_type()
    {
        FeatureBufferContentTypePolicy.Describe(null).Should().Be("(not registered)");
    }
}
