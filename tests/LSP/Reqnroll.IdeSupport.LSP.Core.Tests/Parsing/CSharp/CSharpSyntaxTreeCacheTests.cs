using System.IO;
using AwesomeAssertions;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Parsing.CSharp;

/// <summary>
/// Coverage for issue #491's root-cause fix: repeated resolutions against the same unchanged
/// file must reuse one Roslyn parse rather than re-parsing from scratch on every call, while a
/// genuine change to the file (a rebuild regenerating code-behind, or a live text edit) must
/// still be picked up.
/// </summary>
public class CSharpSyntaxTreeCacheTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CSharpSyntaxTreeCacheTests_" + Guid.NewGuid());

    public CSharpSyntaxTreeCacheTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSource(string content)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid() + ".cs");
        File.WriteAllText(path, content);
        return path;
    }

    // ── GetOrParseFromDisk ───────────────────────────────────────────────────

    [Fact]
    public void GetOrParseFromDisk_returns_null_when_the_file_does_not_exist()
    {
        var sut = new CSharpSyntaxTreeCache();
        var missingPath = Path.Combine(_tempDir, "does-not-exist.cs");

        var result = sut.GetOrParseFromDisk(missingPath, _fileSystem);

        result.Should().BeNull();
    }

    [Fact]
    public void GetOrParseFromDisk_returns_the_same_root_instance_on_repeated_calls_when_the_file_is_unchanged()
    {
        var sut = new CSharpSyntaxTreeCache();
        var path = WriteSource("public class Steps { public void M() { } }");

        var first = sut.GetOrParseFromDisk(path, _fileSystem);
        var second = sut.GetOrParseFromDisk(path, _fileSystem);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "a second read with no mtime change should hit the cache, not re-parse");
    }

    [Fact]
    public void GetOrParseFromDisk_re_parses_after_the_file_is_rewritten()
    {
        var sut = new CSharpSyntaxTreeCache();
        var path = WriteSource("public class Steps { public void First() { } }");

        var first = sut.GetOrParseFromDisk(path, _fileSystem);

        // Ensure a distinct last-write-time even on filesystems with coarse mtime resolution.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        File.WriteAllText(path, "public class Steps { public void Second() { } }");

        var second = sut.GetOrParseFromDisk(path, _fileSystem);

        second.Should().NotBeSameAs(first);
        second!.ToFullString().Should().Contain("Second");
    }

    // ── GetOrParse (live text) ───────────────────────────────────────────────

    [Fact]
    public void GetOrParse_returns_the_same_root_instance_for_the_same_text()
    {
        var sut = new CSharpSyntaxTreeCache();
        const string text = "public class Steps { public void M() { } }";

        var first = sut.GetOrParse("/virtual/Steps.cs", text);
        var second = sut.GetOrParse("/virtual/Steps.cs", text);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void GetOrParse_re_parses_when_the_text_differs()
    {
        var sut = new CSharpSyntaxTreeCache();

        var first = sut.GetOrParse("/virtual/Steps.cs", "public class Steps { public void First() { } }");
        var second = sut.GetOrParse("/virtual/Steps.cs", "public class Steps { public void Second() { } }");

        second.Should().NotBeSameAs(first);
        second.ToFullString().Should().Contain("Second");
    }

    // ── Invalidate ───────────────────────────────────────────────────────────

    [Fact]
    public void Invalidate_forces_a_re_parse_on_the_next_call()
    {
        var sut = new CSharpSyntaxTreeCache();
        const string text = "public class Steps { public void M() { } }";

        var first = sut.GetOrParse("/virtual/Steps.cs", text);
        sut.Invalidate("/virtual/Steps.cs");
        var second = sut.GetOrParse("/virtual/Steps.cs", text);

        second.Should().NotBeSameAs(first, "an explicit invalidation should discard the cached entry even for identical text");
    }

    // ── MRU eviction ─────────────────────────────────────────────────────────

    [Fact]
    public void Evicts_the_least_recently_used_entry_once_the_cap_is_exceeded()
    {
        var sut = new CSharpSyntaxTreeCache();
        const string evictedText = "public class C0 { }";
        var firstRoot = sut.GetOrParse("/virtual/File0.cs", evictedText);

        // Insert enough additional distinct entries to push File0.cs out of the cache's internal
        // 64-entry cap without ever touching it again.
        for (var i = 1; i <= 64; i++)
            sut.GetOrParse($"/virtual/File{i}.cs", $"public class C{i} {{ }}");

        var afterEviction = sut.GetOrParse("/virtual/File0.cs", evictedText);

        afterEviction.Should().NotBeSameAs(firstRoot,
            "the least-recently-used entry should have been evicted once the cap was exceeded");
    }
}
