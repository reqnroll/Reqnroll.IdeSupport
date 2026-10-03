using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Hosting;

public class ClientIdeContextTests
{
    [Fact]
    public void Default_log_level_is_Warning()
    {
        new ClientIdeContext("visualstudio").LogLevel.Should().Be(TraceLevel.Warning);
    }

    [Theory]
    [InlineData(TraceLevel.Off)]
    [InlineData(TraceLevel.Error)]
    [InlineData(TraceLevel.Warning)]
    [InlineData(TraceLevel.Info)]
    [InlineData(TraceLevel.Verbose)]
    public void Explicit_log_level_is_honored(TraceLevel level)
    {
        new ClientIdeContext("vscode", level).LogLevel.Should().Be(level);
    }

    [Fact]
    public void Ide_and_Facets_are_unaffected_by_log_level()
    {
        var context = new ClientIdeContext("visualstudio", TraceLevel.Verbose);

        context.Ide.Should().Be("visualstudio");
        context.Facets.PushesSemanticTokens.Should().BeTrue();
    }

    // ── codeLens/resolve opt-in allowlist (issue #471) ─────────────────────────

    /// <summary>
    /// The allowlist is deliberately empty: no client this repo ships implements the
    /// <c>codeLens/resolve</c> round trip (VS Code's <c>stepCodeLens.ts</c> has no
    /// <c>resolveCodeLens</c> and drops <c>lens.data</c>; Rider's
    /// <c>StepUsagesCodeVisionProvider.kt</c> filters out <c>command == null</c> lenses), so
    /// deferring per-lens computation makes those lenses vanish. Anyone adding an entry here must
    /// ship the client-side resolve support first — this test is the tripwire.
    /// </summary>
    [Theory]
    [InlineData("visualstudio")]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData("VSCode")]
    [InlineData("")]
    [InlineData(null)]
    public void SupportsCodeLensResolve_is_false_for_every_shipped_client(string? ide)
    {
        new ClientIdeContext(ide).Facets.SupportsCodeLensResolve.Should().BeFalse();
    }

    [Fact]
    public void SupportsCodeLensResolve_can_be_forced_on_through_the_test_seam_constructor()
    {
        // Keeps the deferred-resolve branch in the CodeLens handlers reachable from unit tests
        // while the production allowlist stays empty.
        new ClientIdeContext("vscode", new ClientFacets { SupportsCodeLensResolve = true })
            .Facets.SupportsCodeLensResolve.Should().BeTrue();
        new ClientIdeContext("vscode", new ClientFacets { SupportsCodeLensResolve = true }, TraceLevel.Verbose)
            .LogLevel.Should().Be(TraceLevel.Verbose);
    }

    // ── InitializeParams.ClientInfo identity (issue #709) ───────────────────────
    //
    // Two sources feed the IDE identity: the --ide process argument (primary, available before the
    // client connects) and InitializeParams.ClientInfo (secondary, arrives in the initialize
    // request). The tests below pin the precedence rule — the flag always wins, ClientInfo only
    // fills a gap — because a regression here would silently send a VS Code client down Visual
    // Studio's push-based semantic-token path.

    [Fact]
    public void IdeArgument_and_Ide_both_carry_the_flag_when_one_was_passed()
    {
        var context = new ClientIdeContext("vscode");

        context.IdeArgument.Should().Be("vscode");
        context.Ide.Should().Be("vscode");
        context.IdeResolvedFromClientInfo.Should().BeFalse();
    }

    [Fact]
    public void Ide_is_null_before_the_client_reports_ClientInfo()
    {
        var context = new ClientIdeContext(null);

        context.IdeArgument.Should().BeNull();
        context.Ide.Should().BeNull();
        context.ClientName.Should().BeNull();
        context.ClientVersion.Should().BeNull();
        context.IdeResolvedFromClientInfo.Should().BeFalse();
    }

    [Fact]
    public void The_ide_flag_wins_over_a_contradicting_ClientInfo()
    {
        var context = new ClientIdeContext("vscode");

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio", Version = "17.14.0" });

        // The cross-check half of issue #709: the disagreement is recorded (ClientName), but it never
        // changes which IDE the server thinks it is talking to.
        context.Ide.Should().Be("vscode");
        context.Facets.PushesSemanticTokens.Should().BeFalse();
        context.IdeResolvedFromClientInfo.Should().BeFalse();
        context.ClientName.Should().Be("Visual Studio");
        context.ClientVersion.Should().Be("17.14.0");
    }

    [Theory]
    [InlineData("Visual Studio Code", "vscode")]
    [InlineData("Visual Studio Code Insiders", "vscode")]
    [InlineData("vscode", "vscode")]
    [InlineData("Visual Studio", "visualstudio")]
    [InlineData("visualstudio", "visualstudio")]
    [InlineData("JetBrains Rider", "rider")]
    [InlineData("Rider", "rider")]
    public void ClientInfo_fills_in_the_ide_when_no_flag_was_passed(string clientName, string expectedIde)
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = clientName });

        context.Ide.Should().Be(expectedIde);
        context.IdeResolvedFromClientInfo.Should().BeTrue();
    }

    /// <summary>
    /// "Visual Studio Code" contains "Visual Studio", so the mapping has to test the VS Code tokens
    /// first. If the order were ever flipped, every VS Code client would resolve to Visual Studio and
    /// be handed the VS-only reqnroll/semanticTokens push path instead of the standard pull flow.
    /// </summary>
    [Fact]
    public void VS_Code_ClientInfo_names_never_resolve_to_Visual_Studio()
    {
        foreach (var name in new[] { "Visual Studio Code", "Visual Studio Code Insiders", "VSCode" })
        {
            var context = new ClientIdeContext(null);

            context.ApplyClientInfo(new ClientInfo { Name = name });

            context.Facets.PushesSemanticTokens.Should().BeFalse($"'{name}' is a VS Code client");
            context.Facets.RunsVscodeOpenCommandLocally.Should().BeTrue($"'{name}' is a VS Code client");
        }
    }

    [Theory]
    [InlineData("visual studio code")]
    [InlineData("VISUAL STUDIO CODE")]
    [InlineData("VISUAL STUDIO")]
    [InlineData("jetbrains RIDER")]
    public void ClientInfo_matching_is_case_insensitive(string clientName)
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = clientName });

        context.IdeResolvedFromClientInfo.Should().BeTrue();
    }

    [Fact]
    public void An_unrecognized_ClientInfo_name_leaves_the_ide_unresolved()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Some Future Editor" });

        // No guess is better than a wrong one: an unknown name must not be coerced onto a known IDE.
        context.Ide.Should().BeNull();
        context.IdeResolvedFromClientInfo.Should().BeFalse();
        context.ClientName.Should().Be("Some Future Editor");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_ide_flag_counts_as_absent_for_the_fallback(string blankIde)
    {
        var context = new ClientIdeContext(blankIde);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio Code" });

        // A glue component that wired the argument up with an empty value passed no usable identity,
        // so ClientInfo fills it in — but the raw argument is preserved verbatim for the log line.
        context.Ide.Should().Be("vscode");
        context.IdeResolvedFromClientInfo.Should().BeTrue();
        context.IdeArgument.Should().Be(blankIde);
    }

    [Fact]
    public void ClientInfo_without_a_name_records_the_version_but_does_not_resolve_the_ide()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "", Version = "1.2.3" });

        context.Ide.Should().BeNull();
        context.IdeResolvedFromClientInfo.Should().BeFalse();
        context.ClientVersion.Should().Be("1.2.3");
    }

    [Fact]
    public void A_null_ClientInfo_leaves_the_context_untouched()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(null);

        context.Ide.Should().BeNull();
        context.ClientName.Should().BeNull();
        context.ClientVersion.Should().BeNull();
        context.IdeResolvedFromClientInfo.Should().BeFalse();
    }

    [Fact]
    public void A_second_ApplyClientInfo_does_not_overwrite_an_already_resolved_ide()
    {
        var context = new ClientIdeContext(null);

        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio Code" });
        context.ApplyClientInfo(new ClientInfo { Name = "Visual Studio" });

        context.Ide.Should().Be("vscode");
        context.ClientName.Should().Be("Visual Studio");
    }
}
