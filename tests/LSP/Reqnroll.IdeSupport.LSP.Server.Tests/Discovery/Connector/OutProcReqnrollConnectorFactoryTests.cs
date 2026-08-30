using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

public class OutProcReqnrollConnectorFactoryTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly LspIdeScope _ideScope;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public OutProcReqnrollConnectorFactoryTests()
    {
        _ideScope = new LspIdeScope(_logger);
    }

    private OutProcReqnrollConnectorFactory CreateSut() => new(_logger);

    [Fact]
    public void Create_returns_generic_connector_when_no_connector_path_configured()
    {
        var project = DiscoveryTestSupport.MakeProject(_ideScope, _folder, connectorPath: null);

        var connector = CreateSut().Create(project);

        connector.Should().BeOfType<GenericOutProcReqnrollConnector>();
    }

    [Fact]
    public void Create_returns_custom_connector_when_connector_path_configured()
    {
        var project = DiscoveryTestSupport.MakeProject(
            _ideScope, _folder, connectorPath: @"custom\my-connector.dll");

        var connector = CreateSut().Create(project);

        connector.Should().BeOfType<CustomOutProcReqnrollConnector>();
    }

    [Fact]
    public void Create_uses_the_target_framework_from_the_scope()
    {
        // A netfx TFM must still produce a usable connector instance (path selection is
        // exercised separately in GenericOutProcReqnrollConnectorTests).
        var project = DiscoveryTestSupport.MakeProject(
            _ideScope, _folder, targetFrameworkMoniker: ".NETFramework,Version=v4.7.2");

        var connector = CreateSut().Create(project);

        connector.Should().BeOfType<GenericOutProcReqnrollConnector>();
    }
}
