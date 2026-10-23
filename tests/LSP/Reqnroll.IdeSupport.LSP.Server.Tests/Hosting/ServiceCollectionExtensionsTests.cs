using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ServiceCollectionExtensionsTests
{
    // Configuration is per project: a container-wide provider would hand every consumer a default
    // configuration regardless of the project a document belongs to.
    [Fact]
    public void AddReqnrollLspCoreServices_does_not_register_a_container_wide_configuration_provider()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("vscode", TraceLevel.Verbose)
            .BuildServiceProvider();

        provider.GetService<IIdeSupportConfigurationProvider>().Should().BeNull();
    }

    [Fact]
    public void AddReqnrollLspCoreServices_registers_the_requested_log_level_on_ClientIdeContext()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("vscode", TraceLevel.Verbose)
            .BuildServiceProvider();

        var context = provider.GetRequiredService<ClientIdeContext>();

        context.Ide.Should().Be("vscode");
        context.LogLevel.Should().Be(TraceLevel.Verbose);
    }

    [Fact]
    public void AddReqnrollLspCoreServices_defaults_the_log_level_to_Warning()
    {
        var provider = new ServiceCollection()
            .AddReqnrollLspCoreServices("visualstudio")
            .BuildServiceProvider();

        provider.GetRequiredService<ClientIdeContext>().LogLevel.Should().Be(TraceLevel.Warning);
    }
}
