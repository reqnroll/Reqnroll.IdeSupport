using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

public class CSharpFileTextCacheTests
{
    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    [Fact]
    public void TryGet_returns_false_when_nothing_cached()
    {
        var cache = new CSharpFileTextCache();

        cache.TryGet(CsUri, out var text).Should().BeFalse();
        text.Should().BeNull();
    }

    [Fact]
    public void Update_then_TryGet_returns_the_stored_text()
    {
        var cache = new CSharpFileTextCache();

        cache.Update(CsUri, "content v1");

        cache.TryGet(CsUri, out var text).Should().BeTrue();
        text.Should().Be("content v1");
    }

    [Fact]
    public void Update_overwrites_the_previous_text()
    {
        var cache = new CSharpFileTextCache();

        cache.Update(CsUri, "content v1");
        cache.Update(CsUri, "content v2");

        cache.TryGet(CsUri, out var text).Should().BeTrue();
        text.Should().Be("content v2");
    }

    [Fact]
    public void Remove_clears_the_entry()
    {
        var cache = new CSharpFileTextCache();
        cache.Update(CsUri, "content");

        cache.Remove(CsUri);

        cache.TryGet(CsUri, out _).Should().BeFalse();
    }

    [Fact]
    public void All_reflects_every_stored_entry()
    {
        var cache = new CSharpFileTextCache();
        var otherUri = DocumentUri.FromFileSystemPath("/workspace/Other.cs");

        cache.Update(CsUri, "steps content");
        cache.Update(otherUri, "other content");

        cache.All.Should().BeEquivalentTo(new[]
        {
            new CSharpFileText(CsUri, "steps content"),
            new CSharpFileText(otherUri, "other content")
        });
    }
}
