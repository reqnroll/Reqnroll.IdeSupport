using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class IdeBehavioursResolverTests
{
    private static readonly IdeBehaviours VisualStudioBehaviours = new()
    {
        RequiresPushedSemanticTokens = true,
        UsesCustomCodeLensRefresh = true,
        RejectsEmptyTriggerCompletion = true,
        AppliesRenameResponseEditNatively = true,
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

    [Fact]
    public void Context_behaviours_follow_the_ide_flag_from_construction()
    {
        new ClientIdeContext("visualstudio").Behaviours.Should().Be(VisualStudioBehaviours);
        new ClientIdeContext("vscode").Behaviours.Should().Be(VSCodeBehaviours);
        new ClientIdeContext(null).Behaviours.Should().Be(IdeBehaviours.None);
    }

    [Fact]
    public void Context_behaviours_are_recomputed_when_ClientInfo_supplies_the_identity()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio", Version = "17.14.0" });

        context.Behaviours.Should().Be(VisualStudioBehaviours);
    }

    [Fact]
    public void Context_behaviours_ignore_a_contradicting_ClientInfo_when_the_flag_was_passed()
    {
        var context = new ClientIdeContext("vscode");

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio" });

        context.Behaviours.Should().Be(VSCodeBehaviours);
    }

    [Fact]
    public void Context_behaviours_stay_baseline_for_an_unrecognized_ClientInfo_name()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Neovim" });

        context.Behaviours.Should().Be(IdeBehaviours.None);
    }

    [Fact]
    public void Forced_test_seam_behaviours_survive_ApplyClientInfo()
    {
        var forced = new IdeBehaviours { SupportsCodeLensResolve = true };
        var context = new ClientIdeContext(null, forced);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio", Version = "17.14.0" });

        context.Behaviours.Should().Be(forced);
    }
}
