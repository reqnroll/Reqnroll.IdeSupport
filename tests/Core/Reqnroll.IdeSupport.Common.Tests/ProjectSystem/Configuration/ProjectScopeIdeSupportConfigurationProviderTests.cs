using Reqnroll.IdeSupport.Common.Tests.TestHelpers;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem.Configuration;

/// <summary>
/// Issue #967: each configuration source must be applied atomically - a source that fails to load or
/// validate must not leave partial changes behind, and must not discard the settings of valid sources.
/// </summary>
public class ProjectScopeIdeSupportConfigurationProviderTests
{
    private const string ProjectFolder = @"X:\proj";

    private readonly MockFileSystemForTests _fileSystem = new();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    public ProjectScopeIdeSupportConfigurationProviderTests()
    {
        _fileSystem.AddDirectory(ProjectFolder);
    }

    private void AddFile(string name, string content) =>
        _fileSystem.AddFile(Path.Combine(ProjectFolder, name), new MockFileData(content));

    private IdeSupportConfiguration Load()
    {
        var ideScope = Substitute.For<IIdeScope>();
        ideScope.FileSystem.Returns(_fileSystem);
        ideScope.Logger.Returns(_logger);
        var projectScope = Substitute.For<IProjectScope>();
        projectScope.IdeScope.Returns(ideScope);
        projectScope.ProjectFolder.Returns(ProjectFolder);
        projectScope.ProjectFullName.Returns(Path.Combine(ProjectFolder, "proj.csproj"));
        return new ProjectScopeIdeSupportConfigurationProvider(projectScope).GetConfiguration();
    }

    [Fact]
    public void A_source_with_an_invalid_version_does_not_discard_settings_from_other_valid_sources()
    {
        AddFile("reqnroll.json", """{ "ide": { "reqnroll": { "version": "not-a-version" } } }""");
        AddFile("deveroom.json", """{ "defaultFeatureLanguage": "de-DE" }""");

        var config = Load();

        config.DefaultFeatureLanguage.Should().Be("de-DE");
        config.Reqnroll.Version.Should().BeNull("the failing source must not leave its invalid value behind");
        config.ConfigurationChangeTime.Should().BeAfter(DateTimeOffset.MinValue);
    }

    [Fact]
    public void A_specsync_source_with_an_invalid_tag_pattern_does_not_discard_settings_from_reqnroll_json()
    {
        AddFile("reqnroll.json", """{ "language": { "feature": "fr-FR" } }""");
        AddFile("specsync.json", """
            { "remote": { "projectUrl": "https://example.test/proj" },
              "synchronization": { "testCaseTagPrefix": "[" } }
            """);

        var config = Load();

        config.DefaultFeatureLanguage.Should().Be("fr-FR");
        config.Traceability.TagLinks.Should().BeEmpty("the failing source must not leave its tag links behind");
    }

    [Fact]
    public void A_source_that_fails_after_being_populated_leaves_no_partial_changes()
    {
        // The language is populated first; the missing configFilePath then fails EnsureFullPath.
        AddFile("reqnroll.json", """
            { "language": { "feature": "de-DE" },
              "ide": { "reqnroll": { "configFilePath": "missing.json" } } }
            """);

        var config = Load();

        config.DefaultFeatureLanguage.Should().Be("en-US");
        config.ConfigurationBaseFolder.Should().BeNull();
        config.Reqnroll.ConfigFilePath.Should().BeNull();
    }

    [Fact]
    public void A_failing_source_is_reported_with_a_warning()
    {
        AddFile("reqnroll.json", """{ "ide": { "reqnroll": { "version": "not-a-version" } } }""");

        Load();

        _logger.Received().Log(Arg.Is<LogMessage>(m => m.Level == TraceLevel.Warning));
    }

    [Fact]
    public void Valid_sources_still_layer_in_order()
    {
        AddFile("reqnroll.json", """{ "language": { "feature": "fr-FR", "binding": "fr-FR" } }""");
        AddFile("deveroom.json", """{ "defaultFeatureLanguage": "de-DE" }""");

        var config = Load();

        config.DefaultFeatureLanguage.Should().Be("de-DE");
        config.ConfiguredBindingCulture.Should().Be("fr-FR");
    }

    [Fact]
    public void A_single_source_populates_array_properties()
    {
        AddFile("reqnroll.json", """
            { "ide": { "traceability": { "tagLinks": [ { "tagPattern": "a:(?<id>[0-9]+)", "urlTemplate": "https://a.test/{id}" } ] } } }
            """);

        Load().Traceability.TagLinks.Select(t => t.TagPattern).Should().Equal("a:(?<id>[0-9]+)");
    }

    [Fact]
    public void Array_properties_populated_by_several_sources_are_replaced_not_appended()
    {
        // Empirical record (issue #967 claimed PopulateObject appends): with the sources' arrays populated
        // into the same instance, the later source's array replaces the earlier one. Pinned so a change in
        // Newtonsoft behaviour or in the loading approach is noticed.
        AddFile("reqnroll.json", """
            { "ide": { "traceability": { "tagLinks": [ { "tagPattern": "a:(?<id>[0-9]+)", "urlTemplate": "https://a.test/{id}" } ] } } }
            """);
        AddFile("deveroom.json", """
            { "traceability": { "tagLinks": [ { "tagPattern": "b:(?<id>[0-9]+)", "urlTemplate": "https://b.test/{id}" } ] } }
            """);

        Load().Traceability.TagLinks.Select(t => t.TagPattern).Should().Equal("b:(?<id>[0-9]+)");
    }
}
