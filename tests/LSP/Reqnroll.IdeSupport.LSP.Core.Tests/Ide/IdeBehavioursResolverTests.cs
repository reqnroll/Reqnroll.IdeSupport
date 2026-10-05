using Reqnroll.IdeSupport.LSP.Core.Ide;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Ide;

public class IdeBehavioursResolverTests
{
    private static readonly IdeBehaviours VisualStudioBehaviours = new()
    {
        RequiresPushedSemanticTokens = true,
        UsesCustomCodeLensRefresh = true,
        RejectsEmptyTriggerCompletion = true,
        RequestsCompletionAfterDeletion = true,
        AppliesRenameResponseEditNatively = true,
        NavigatesStepsViaFindStepDefinitions = true,
    };

    private static readonly IdeBehaviours VSCodeBehaviours = new()
    {
        RunsVscodeOpenCommandLocally = true,
        HonorsShowDocumentRequests = true,
    };

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("VisualStudio")]
    public void Visual_Studio_gets_its_push_and_workaround_behaviours(string ide)
    {
        IdeBehavioursResolver.Resolve(ide, null).Should().Be(VisualStudioBehaviours);
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("VSCode")]
    public void VS_Code_gets_the_vscode_open_behaviours_and_none_of_the_VS_workarounds(string ide)
    {
        IdeBehavioursResolver.Resolve(ide, null).Should().Be(VSCodeBehaviours);
    }

    [Theory]
    [InlineData("rider")]
    [InlineData("something-new")]
    [InlineData("")]
    [InlineData(null)]
    public void Rider_and_unrecognized_clients_get_the_standard_LSP_baseline(string? ide)
    {
        IdeBehavioursResolver.Resolve(ide, null).Should().Be(IdeBehaviours.None);
    }

    [Theory]
    [InlineData("visualstudio", "17.14.0")]
    [InlineData("visualstudio", "")]
    [InlineData("vscode", "1.99.0")]
    public void Behaviours_do_not_currently_vary_by_client_version(string ide, string version)
    {
        IdeBehavioursResolver.Resolve(ide, version).Should().Be(IdeBehavioursResolver.Resolve(ide, null));
    }

    [Fact]
    public void No_shipped_client_supports_codeLens_resolve()
    {
        foreach (var ide in new[] { "visualstudio", "vscode", "rider" })
            IdeBehavioursResolver.Resolve(ide, null).SupportsCodeLensResolve.Should().BeFalse(ide);
    }
}
