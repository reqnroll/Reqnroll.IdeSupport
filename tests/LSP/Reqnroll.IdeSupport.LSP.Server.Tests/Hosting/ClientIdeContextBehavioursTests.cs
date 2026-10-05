using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Core.Ide;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ClientIdeContextBehavioursTests
{
    private static readonly IdeBehaviours VisualStudioBehaviours = new()
    {
        RequiresPushedSemanticTokens = true,
        UsesCustomCodeLensRefresh = true,
        RejectsEmptyTriggerCompletion = true,
        RequestsCompletionAfterDeletion = true,
        AppliesRenameResponseEditNatively = true,
    };

    private static readonly IdeBehaviours VSCodeBehaviours = new()
    {
        RunsVscodeOpenCommandLocally = true,
        HonorsShowDocumentRequests = true,
    };

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
