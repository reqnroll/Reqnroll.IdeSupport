namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem.Configuration;

using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;

/// <summary>
/// Regression tests for <see cref="ProjectScopeIdeSupportConfigurationProvider.UpdateFromSpecSyncJsonConfig"/>:
/// the SpecSync tag prefix (<c>synchronization/testCaseTagPrefix</c> and each
/// <c>synchronization/links[]/tagPrefix</c>) is interpolated into the tag-link regex, so it must be
/// escaped first. Without escaping, a prefix containing a regex metacharacter either matches a tag it
/// should not (a <c>.</c> matching any character) or produces an invalid pattern, in which case
/// <see cref="TagLinkConfiguration.CheckConfiguration"/> throws and the whole configuration is
/// discarded (issue #966).
/// </summary>
public class ProjectScopeIdeSupportConfigurationProviderSpecSyncTests
{
    private const string ProjectUrl = "https://dev.azure.com/example/project";

    private static IdeSupportConfiguration Load(string specSyncJson)
    {
        var configuration = new IdeSupportConfiguration();
        ProjectScopeIdeSupportConfigurationProvider.UpdateFromSpecSyncJsonConfig(configuration, specSyncJson);
        configuration.Traceability.CheckConfiguration();
        return configuration;
    }

    private static string Json(string? testCaseTagPrefix = null, string? linkTagPrefix = null)
    {
        var synchronization = new List<string>();
        if (testCaseTagPrefix != null)
            synchronization.Add($"\"testCaseTagPrefix\": \"{testCaseTagPrefix}\"");
        if (linkTagPrefix != null)
            synchronization.Add($"\"links\": [ {{ \"tagPrefix\": \"{linkTagPrefix}\" }} ]");

        return $$"""
            {
              "remote": { "projectUrl": "{{ProjectUrl}}" },
              "synchronization": { {{string.Join(", ", synchronization)}} }
            }
            """;
    }

    [Fact]
    public void Default_tc_prefix_still_resolves_unchanged()
    {
        var configuration = Load(Json());

        Assert.Equal(new Uri(ProjectUrl + "/_workitems/edit/123"), configuration.Traceability.ResolveTagLink("@tc:123"));
        Assert.Null(configuration.Traceability.ResolveTagLink("@smoke"));
    }

    [Fact]
    public void Prefix_with_dot_is_escaped_and_does_not_match_a_look_alike_tag()
    {
        var configuration = Load(Json(testCaseTagPrefix: "tc."));

        // The intended tag resolves...
        Assert.Equal(new Uri(ProjectUrl + "/_workitems/edit/7"), configuration.Traceability.ResolveTagLink("@tc.:7"));

        // ...but the dot must be a literal dot, not "any character".
        Assert.Null(configuration.Traceability.ResolveTagLink("@tcX:7"));
    }

    [Fact]
    public void Escaped_dot_prefix_produces_a_literal_dot_in_the_pattern()
    {
        var configuration = Load(Json(testCaseTagPrefix: "tc."));

        Assert.Equal(@"tc\.\:(?<id>\d+)", configuration.Traceability.TagLinks[0].TagPattern);
    }

    [Fact]
    public void Prefix_with_unbalanced_parenthesis_does_not_make_the_pattern_invalid()
    {
        // CheckConfiguration() runs inside Load(); on the unescaped code the "tc(" prefix produced the
        // invalid pattern "tc(\:(?<id>\d+)" and threw IdeSupportConfigurationException here.
        var configuration = Load(Json(testCaseTagPrefix: "tc("));

        Assert.Equal(new Uri(ProjectUrl + "/_workitems/edit/9"), configuration.Traceability.ResolveTagLink("@tc(:9"));
    }

    [Fact]
    public void Link_tag_prefix_with_metacharacters_is_escaped_too()
    {
        var configuration = Load(Json(linkTagPrefix: "bug+"));

        // The "+" is escaped to a literal plus...
        Assert.Equal(new Uri(ProjectUrl + "/_workitems/edit/42"), configuration.Traceability.ResolveTagLink("@bug+:42"));

        // ...rather than acting as a regex quantifier.
        Assert.Null(configuration.Traceability.ResolveTagLink("@bugggg:42"));
    }
}
