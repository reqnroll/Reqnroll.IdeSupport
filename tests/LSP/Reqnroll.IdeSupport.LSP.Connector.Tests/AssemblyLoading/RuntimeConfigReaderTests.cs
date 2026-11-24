using ReqnrollConnector.AssemblyLoading.dotNET;
using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.AssemblyLoading;

public sealed class RuntimeConfigReaderTests : IDisposable
{
    private readonly ILogger _log = Substitute.For<ILogger>();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reqnroll-rcr-" + Guid.NewGuid().ToString("N"));

    public RuntimeConfigReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Parse_reads_a_single_framework()
    {
        var frameworks = RuntimeConfigReader.Parse(
            """{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }""");

        frameworks.Should().Equal(new SharedFrameworkReference("Microsoft.NETCore.App", new Version(8, 0, 0)));
    }

    [Fact]
    public void Parse_reads_several_frameworks()
    {
        var frameworks = RuntimeConfigReader.Parse(
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
                  { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
                ]
              }
            }
            """);

        frameworks.Should().Equal(
            new SharedFrameworkReference("Microsoft.NETCore.App", new Version(10, 0, 0)),
            new SharedFrameworkReference("Microsoft.AspNetCore.App", new Version(10, 0, 0)));
    }

    [Fact]
    public void Parse_ignores_a_prerelease_suffix_and_skips_invalid_entries_including_path_like_names()
    {
        var frameworks = RuntimeConfigReader.Parse(
            """
            {
              "runtimeOptions": {
                "frameworks": [
                  { "name": "Microsoft.AspNetCore.App", "version": "10.0.0-rc.1.25451.107" },
                  { "name": "NoVersion" },
                  { "version": "10.0.0" },
                  { "name": "BadVersion", "version": "latest" },
                  { "name": "..", "version": "10.0.0" },
                  { "name": "../../Elsewhere", "version": "10.0.0" },
                  { "name": "Nested\\Framework", "version": "10.0.0" }
                ]
              }
            }
            """);

        frameworks.Should().Equal(new SharedFrameworkReference("Microsoft.AspNetCore.App", new Version(10, 0, 0)));
    }

    [Fact]
    public void Parse_returns_nothing_for_a_self_contained_config_without_frameworks()
    {
        RuntimeConfigReader.Parse("""{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [] } }""")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("10.0", 10, 0, 0)]
    [InlineData("10.0.15", 10, 0, 15)]
    [InlineData("9.0.0-preview.7.24405.7", 9, 0, 0)]
    public void ParseFrameworkVersion_normalizes_to_three_parts(string value, int major, int minor, int patch)
    {
        RuntimeConfigReader.ParseFrameworkVersion(value).Should().Be(new Version(major, minor, patch));
    }

    [Fact]
    public void ReadFrameworkReferences_reads_the_runtimeconfig_next_to_the_assembly()
    {
        var assemblyPath = Path.Combine(_directory, "My.Specs.dll");
        File.WriteAllText(Path.Combine(_directory, "My.Specs.runtimeconfig.json"),
            """{ "runtimeOptions": { "frameworks": [ { "name": "Microsoft.AspNetCore.App", "version": "8.0.0" } ] } }""");

        RuntimeConfigReader.ReadFrameworkReferences(assemblyPath, _log)
            .Should().Equal(new SharedFrameworkReference("Microsoft.AspNetCore.App", new Version(8, 0, 0)));
    }

    [Fact]
    public void ReadFrameworkReferences_returns_nothing_when_there_is_no_runtimeconfig()
    {
        RuntimeConfigReader.ReadFrameworkReferences(Path.Combine(_directory, "Library.dll"), _log)
            .Should().BeEmpty();
    }

    [Fact]
    public void ReadFrameworkReferences_returns_nothing_for_malformed_json_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_directory, "Broken.runtimeconfig.json"), "{ not json");

        RuntimeConfigReader.ReadFrameworkReferences(Path.Combine(_directory, "Broken.dll"), _log)
            .Should().BeEmpty();
    }
}
