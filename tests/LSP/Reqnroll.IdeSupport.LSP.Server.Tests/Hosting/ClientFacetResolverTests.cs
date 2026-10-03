using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ClientFacetResolverTests
{
    private static readonly ClientFacets VisualStudioFacets = new()
    {
        RequiresPushedSemanticTokens = true,
        UsesCustomCodeLensRefresh = true,
        RejectsEmptyTriggerCompletion = true,
        AppliesRenameResponseEditNatively = true,
    };

    private static readonly ClientFacets VSCodeFacets = new()
    {
        RunsVscodeOpenCommandLocally = true,
        HonorsShowDocumentRequests = true,
    };

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("VisualStudio")]
    public void Visual_Studio_gets_its_push_and_workaround_facets(string ide)
    {
        ClientFacetResolver.Resolve(ide, null).Should().Be(VisualStudioFacets);
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("VSCode")]
    public void VS_Code_gets_the_vscode_open_facets_and_none_of_the_VS_workarounds(string ide)
    {
        ClientFacetResolver.Resolve(ide, null).Should().Be(VSCodeFacets);
    }

    [Theory]
    [InlineData("rider")]
    [InlineData("something-new")]
    [InlineData("")]
    [InlineData(null)]
    public void Rider_and_unrecognized_clients_get_the_standard_LSP_baseline(string? ide)
    {
        ClientFacetResolver.Resolve(ide, null).Should().Be(ClientFacets.None);
    }

    [Theory]
    [InlineData("visualstudio", "17.14.0")]
    [InlineData("visualstudio", "")]
    [InlineData("vscode", "1.99.0")]
    public void Facets_do_not_currently_vary_by_client_version(string ide, string version)
    {
        ClientFacetResolver.Resolve(ide, version).Should().Be(ClientFacetResolver.Resolve(ide, null));
    }

    [Fact]
    public void No_shipped_client_supports_codeLens_resolve()
    {
        foreach (var ide in new[] { "visualstudio", "vscode", "rider" })
            ClientFacetResolver.Resolve(ide, null).SupportsCodeLensResolve.Should().BeFalse(ide);
    }

    [Fact]
    public void Context_facets_follow_the_ide_flag_from_construction()
    {
        new ClientIdeContext("visualstudio").Facets.Should().Be(VisualStudioFacets);
        new ClientIdeContext("vscode").Facets.Should().Be(VSCodeFacets);
        new ClientIdeContext(null).Facets.Should().Be(ClientFacets.None);
    }

    [Fact]
    public void Context_facets_are_recomputed_when_ClientInfo_supplies_the_identity()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio", Version = "17.14.0" });

        context.Facets.Should().Be(VisualStudioFacets);
    }

    [Fact]
    public void Context_facets_ignore_a_contradicting_ClientInfo_when_the_flag_was_passed()
    {
        var context = new ClientIdeContext("vscode");

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio" });

        context.Facets.Should().Be(VSCodeFacets);
    }

    [Fact]
    public void Context_facets_stay_baseline_for_an_unrecognized_ClientInfo_name()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Neovim" });

        context.Facets.Should().Be(ClientFacets.None);
    }

    [Fact]
    public void Forced_test_seam_facets_survive_ApplyClientInfo()
    {
        var forced = new ClientFacets { SupportsCodeLensResolve = true };
        var context = new ClientIdeContext(null, forced);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio", Version = "17.14.0" });

        context.Facets.Should().Be(forced);
    }
}
