using ReqnrollConnector.AssemblyLoading.dotNET;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.AssemblyLoading;

public class DotnetRootLocatorTests
{
    [Fact]
    public void The_running_installation_root_contains_the_running_Microsoft_NETCore_App()
    {
        var root = DotnetRootLocator.GetRunningInstallationRoot();

        root.Should().NotBeNull("the tests run on a framework-dependent .NET installation");
        var coreLibDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        Path.GetFullPath(coreLibDirectory).Should().StartWith(
            Path.Combine(Path.GetFullPath(root!), "shared", "Microsoft.NETCore.App"));
    }

    [Fact]
    public void Candidate_roots_start_with_the_running_installation_and_have_no_duplicates()
    {
        var roots = DotnetRootLocator.GetCandidateRoots();

        roots.Should().NotBeEmpty();
        roots[0].Should().BeEquivalentTo(
            Path.GetFullPath(DotnetRootLocator.GetRunningInstallationRoot()!)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        roots.Should().OnlyHaveUniqueItems();
    }
}
