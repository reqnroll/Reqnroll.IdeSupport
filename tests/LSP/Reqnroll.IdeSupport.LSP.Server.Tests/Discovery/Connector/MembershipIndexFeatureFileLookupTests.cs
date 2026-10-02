using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Covers the membership-index-backed feature-file lookup the Reqnroll-test-project gate uses
/// (issue #731).
/// </summary>
public class MembershipIndexFeatureFileLookupTests
{
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();

    private MembershipIndexFeatureFileLookup CreateSut() => new(_scopeManager);

    private static LspReqnrollProject MakeProject() =>
        new(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder = @"C:\repos\MyApp",
            ProjectFile = @"C:\repos\MyApp\tests\MyApp.Tests\MyApp.Tests.csproj",
            ProjectFolder = @"C:\repos\MyApp\tests\MyApp.Tests",
            TargetFrameworkMoniker = ".NETCoreApp,Version=v10.0"
        }, Substitute.For<IIdeScope>());

    [Fact]
    public void Reports_true_when_the_baseline_lists_feature_files()
    {
        var project = MakeProject();
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(project)
            .Returns([@"C:\repos\MyApp\specs\Calculator.feature"]);

        CreateSut().HasFeatureFiles(project).Should().BeTrue();
    }

    [Fact]
    public void Reports_false_when_the_baseline_lists_none()
    {
        var project = MakeProject();
        _scopeManager.HasBaselineForProject(project).Returns(true);
        _scopeManager.GetIndexedFeatureFiles(project).Returns([]);

        CreateSut().HasFeatureFiles(project).Should().BeFalse();
    }

    [Fact]
    public void Reports_unknown_until_the_baseline_arrives()
    {
        // Before the baseline lands the index legitimately reports zero files for a project full
        // of them, so this has to be "unknown" — reporting false would skip a real test project.
        var project = MakeProject();
        _scopeManager.HasBaselineForProject(project).Returns(false);
        _scopeManager.GetIndexedFeatureFiles(project).Returns([]);

        CreateSut().HasFeatureFiles(project).Should().BeNull();
    }

    [Fact]
    public void Reports_unknown_for_a_scope_that_is_not_an_lsp_project()
    {
        // e.g. the scope the spec fixtures build directly; there is no membership index entry to
        // consult, so the gate must fall back to running discovery.
        CreateSut().HasFeatureFiles(Substitute.For<IProjectScope>()).Should().BeNull();
    }
}
