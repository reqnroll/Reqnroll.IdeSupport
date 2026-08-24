using System.Diagnostics;
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
    public void Ide_and_IsVisualStudio_are_unaffected_by_log_level()
    {
        var context = new ClientIdeContext("visualstudio", TraceLevel.Verbose);

        context.Ide.Should().Be("visualstudio");
        context.IsVisualStudio.Should().BeTrue();
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
        new ClientIdeContext(ide).SupportsCodeLensResolve.Should().BeFalse();
    }

    [Fact]
    public void SupportsCodeLensResolve_can_be_forced_on_through_the_test_seam_constructor()
    {
        // Keeps the deferred-resolve branch in the CodeLens handlers reachable from unit tests
        // while the production allowlist stays empty.
        new ClientIdeContext("vscode", supportsCodeLensResolve: true)
            .SupportsCodeLensResolve.Should().BeTrue();
        new ClientIdeContext("vscode", supportsCodeLensResolve: true, TraceLevel.Verbose)
            .LogLevel.Should().Be(TraceLevel.Verbose);
    }
}
