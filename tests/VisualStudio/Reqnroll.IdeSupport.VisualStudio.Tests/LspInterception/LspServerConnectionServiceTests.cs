using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.LspInterception;

/// <summary>
/// <see cref="LspServerConnectionService"/> launches a process, wires
/// <c>Nerdbank.Streams</c> pipes, and starts its eager startup task via
/// <c>ThreadHelper.JoinableTaskFactory</c> from its constructor — all of that requires a VS host
/// and is not unit-testable here (see project convention noted in
/// <c>CodeLensRefreshInterceptorTests</c>). <see cref="LspServerConnectionService.ResolveServerExePath"/>
/// was extracted as a pure static method specifically so the one piece of testable logic (bundled
/// server exe path resolution) has coverage independent of process/VS-host concerns.
/// </summary>
public class LspServerConnectionServiceTests
{
    [Fact]
    public void Resolves_the_server_exe_under_an_LSPServer_subfolder_of_the_extension_assembly()
    {
        var extensionAssemblyLocation = @"C:\ext\Reqnroll.IdeSupport.VisualStudio.Extension.dll";

        var path = LspServerConnectionService.ResolveServerExePath(extensionAssemblyLocation);

        path.Should().Be(@"C:\ext\LSPServer\Reqnroll.IdeSupport.LSP.Server.exe");
    }

    [Fact]
    public void Resolution_is_relative_to_the_assembly_directory_not_the_working_directory()
    {
        var extensionAssemblyLocation = @"D:\some\other\deep\path\Ext.dll";

        var path = LspServerConnectionService.ResolveServerExePath(extensionAssemblyLocation);

        path.Should().Be(@"D:\some\other\deep\path\LSPServer\Reqnroll.IdeSupport.LSP.Server.exe");
    }

    [Fact]
    public void Server_arguments_identify_the_ide_and_set_all_three_logging_dials()
    {
        // The test assembly and the extension assembly share the same build configuration, so the
        // #if DEBUG branch actually taken here is whatever configuration this test itself was
        // compiled under.
#if DEBUG
        LspServerConnectionService.ServerArguments.Should().Be(
            "--ide visualstudio --log-level Verbose --protocol-log-level Info --trace Verbose");
#else
        LspServerConnectionService.ServerArguments.Should().Be(
            "--ide visualstudio --log-level Warning --protocol-log-level Warning --trace Off");
#endif
    }
}
