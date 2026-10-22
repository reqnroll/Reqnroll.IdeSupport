using AwesomeAssertions;
using Reqnroll.IdeSupport.Common.Configuration;
using Xunit;

namespace Reqnroll.IdeSupport.Common.Tests.Configuration;

public class ConfigurationFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rnr-cfgfiles-" + Guid.NewGuid().ToString("N"));

    public ConfigurationFilesTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Every_file_the_provider_can_load_is_watched()
    {
        ConfigurationFiles.WatchedFileNames.Should().BeEquivalentTo(
            "reqnroll.json", "specflow.json", "App.config", "specsync.json", "deveroom.json");
    }

    [Theory]
    [InlineData("reqnroll.json", ConfigurationFileFormat.ReqnrollJson)]
    [InlineData("specflow.json", ConfigurationFileFormat.ReqnrollJson)]
    [InlineData("App.config", ConfigurationFileFormat.SpecFlowXml)]
    [InlineData("specsync.json", ConfigurationFileFormat.SpecSyncJson)]
    [InlineData("deveroom.json", ConfigurationFileFormat.IdeSupportJson)]
    public void Each_file_is_read_with_its_own_format(string fileName, ConfigurationFileFormat format)
    {
        ConfigurationFiles.TryGetDefinition(Path.Combine(_folder, fileName.ToUpperInvariant()), out var definition)
            .Should().BeTrue("file names match case-insensitively");

        definition.Format.Should().Be(format);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("reqnroll.json.bak")]
    public void Other_files_are_not_configuration_files(string fileName)
    {
        ConfigurationFiles.IsConfigurationFile(Path.Combine(_folder, fileName)).Should().BeFalse();
    }

    [Fact]
    public void The_primary_file_is_the_first_existing_of_reqnroll_json_specflow_json_then_App_config()
    {
        var fileSystem = new FileSystemForIDE();
        File.WriteAllText(Path.Combine(_folder, "App.config"), "<configuration/>");
        ConfigurationFiles.FindPrimaryConfigFile(fileSystem, _folder).Should().EndWith("App.config");

        File.WriteAllText(Path.Combine(_folder, "specflow.json"), "{}");
        ConfigurationFiles.FindPrimaryConfigFile(fileSystem, _folder).Should().EndWith("specflow.json");

        File.WriteAllText(Path.Combine(_folder, "reqnroll.json"), "{}");
        ConfigurationFiles.FindPrimaryConfigFile(fileSystem, _folder).Should().EndWith("reqnroll.json");
    }
}
