using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.LSP.Server.Features.TestOutcomes;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestOutcomes;

/// <summary>
/// Covers <see cref="TestOutcomeTcpListener.ResolveWorkspaceRoot"/>'s <c>RootPath</c> fallback —
/// live-verified against VS: its LSP client only advertises the <c>workspaceFolders</c>
/// <em>capability</em>, it never actually sends the <c>workspaceFolders</c> array param, so relying on
/// <c>ClientSettings.WorkspaceFolders</c> alone resolved to null for every VS session and silently
/// disabled the whole MTP breadcrumb-matching mechanism even once everything else in that pipeline
/// worked (the reporter's <c>SessionBreadcrumbMatcher.FindBestMatch</c> skips any candidate whose
/// <c>WorkspaceRoot</c> is null).
/// </summary>
public class TestOutcomeTcpListenerResolveWorkspaceRootTests
{
    private readonly ILanguageServerFacade _languageServer = Substitute.For<ILanguageServerFacade>();

    private static WorkspaceFolder FolderFrom(string path) =>
        new() { Uri = DocumentUri.FromFileSystemPath(path), Name = Path.GetFileName(path) };

    [Fact]
    public void Prefers_the_first_non_empty_WorkspaceFolders_entry_when_present()
    {
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(FolderFrom(@"c:\repo\MySolution")),
            RootPath = @"C:\repo\SomeOtherRoot",
        });

        TestOutcomeTcpListener.ResolveWorkspaceRoot(_languageServer).Should().Be(@"c:\repo\MySolution");
    }

    [Fact]
    public void Falls_back_to_RootPath_when_WorkspaceFolders_is_null()
    {
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            WorkspaceFolders = null,
            RootPath = @"c:\repo\MySolution",
        });

        // DocumentUri round-trips RootPath through a file:// URI, which normalizes the drive letter to
        // lowercase — hence the lowercase "c:" expectation, not a claim about this fallback's own behavior.
        TestOutcomeTcpListener.ResolveWorkspaceRoot(_languageServer).Should().Be(@"c:\repo\MySolution");
    }

    [Fact]
    public void Falls_back_to_RootPath_when_WorkspaceFolders_is_empty()
    {
        // The exact shape live-verified against VS's own LSP client: it advertises the
        // workspaceFolders *capability* but never sends the array param itself.
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(),
            RootPath = @"c:\repo\MySolution",
        });

        TestOutcomeTcpListener.ResolveWorkspaceRoot(_languageServer).Should().Be(@"c:\repo\MySolution");
    }

    [Fact]
    public void Returns_null_when_neither_WorkspaceFolders_nor_RootPath_is_available()
    {
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            WorkspaceFolders = null,
        });

        TestOutcomeTcpListener.ResolveWorkspaceRoot(_languageServer).Should().BeNull();
    }
}
