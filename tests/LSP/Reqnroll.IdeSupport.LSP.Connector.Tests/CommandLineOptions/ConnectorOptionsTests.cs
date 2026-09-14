using ReqnrollConnector.CommandLineOptions;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.CommandLineOptions;

public class ConnectorOptionsTests
{
    [Fact]
    public void Parse_strips_the_debug_flag_and_still_resolves_the_assembly_path()
    {
        var options = ConnectorOptions.Parse(["discovery", "--debug", "/some/Assembly.dll"]);

        var discovery = options.Should().BeOfType<DiscoveryOptions>().Subject;
        discovery.DebugMode.Should().BeTrue();
        discovery.AssemblyFile.Should().EndWith("Assembly.dll");
    }

    [Fact]
    public void Parse_strips_the_file_log_flag_and_still_resolves_the_assembly_path()
    {
        // issue #637: --file-log must not be misread as the assembly path.
        var options = ConnectorOptions.Parse(["discovery", "--file-log", "/some/Assembly.dll"]);

        var discovery = options.Should().BeOfType<DiscoveryOptions>().Subject;
        discovery.AssemblyFile.Should().EndWith("Assembly.dll");
    }

    [Fact]
    public void Parse_strips_both_debug_and_file_log_flags_regardless_of_order()
    {
        var options = ConnectorOptions.Parse(["discovery", "--file-log", "--debug", "/some/Assembly.dll"]);

        var discovery = options.Should().BeOfType<DiscoveryOptions>().Subject;
        discovery.DebugMode.Should().BeTrue();
        discovery.AssemblyFile.Should().EndWith("Assembly.dll");
    }

    [Fact]
    public void Parse_without_either_flag_defaults_DebugMode_to_false()
    {
        var options = ConnectorOptions.Parse(["discovery", "/some/Assembly.dll"]);

        var discovery = options.Should().BeOfType<DiscoveryOptions>().Subject;
        discovery.DebugMode.Should().BeFalse();
        discovery.AssemblyFile.Should().EndWith("Assembly.dll");
    }

    [Fact]
    public void Parse_resolves_the_config_path_after_stripping_flags()
    {
        var options = ConnectorOptions.Parse(
            ["discovery", "--file-log", "/some/Assembly.dll", "/some/reqnroll.json"]);

        var discovery = options.Should().BeOfType<DiscoveryOptions>().Subject;
        discovery.ConfigFile.Should().EndWith("reqnroll.json");
    }
}
