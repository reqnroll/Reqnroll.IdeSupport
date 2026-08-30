using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

public class DocumentBufferServiceTests
{
    private static DocumentUri MakeUri(string path = "/workspace/test.feature")
        => DocumentUri.FromFileSystemPath(path);

    private static DocumentBufferService CreateSut() => new();

    // ── Update / TryGet ───────────────────────────────────────────────────────

    [Fact]
    public void TryGet_returns_false_when_uri_not_registered()
    {
        var sut = CreateSut();
        sut.TryGet(MakeUri(), out var buffer).Should().BeFalse();
        buffer.Should().BeNull();
    }

    [Fact]
    public void Update_then_TryGet_returns_buffer()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        sut.Update(uri, 1, "Feature: X\n");

        sut.TryGet(uri, out var buffer).Should().BeTrue();
        buffer.Should().NotBeNull();
        buffer!.Text.Should().Be("Feature: X\n");
        buffer.Version.Should().Be(1);
    }

    [Fact]
    public void Update_overwrites_existing_buffer()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        sut.Update(uri, 1, "Feature: Old\n");
        sut.Update(uri, 2, "Feature: New\n");

        sut.TryGet(uri, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be("Feature: New\n");
        buffer.Version.Should().Be(2);
    }

    [Fact]
    public void Update_with_null_version_is_stored()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        sut.Update(uri, null, "text");

        sut.TryGet(uri, out var buffer).Should().BeTrue();
        buffer!.Version.Should().BeNull();
    }

    // ── UpdateTags ────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateTags_on_existing_buffer_sets_Tags()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        sut.Update(uri, 3, "Feature: X\n");
        var tags = Array.Empty<Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.DeveroomTag>();
        sut.UpdateTags(uri, tags);

        sut.TryGet(uri, out var buffer).Should().BeTrue();
        buffer!.Tags.Should().BeSameAs(tags);
        buffer.Version.Should().Be(3);
        buffer.Text.Should().Be("Feature: X\n");
    }

    [Fact]
    public void UpdateTags_without_prior_Update_creates_buffer()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        var tags = Array.Empty<Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.DeveroomTag>();
        sut.UpdateTags(uri, tags);

        sut.TryGet(uri, out var buffer).Should().BeTrue();
        buffer!.Tags.Should().BeSameAs(tags);
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_deletes_registered_buffer()
    {
        var sut = CreateSut();
        var uri = MakeUri();
        sut.Update(uri, 1, "text");
        sut.Remove(uri);

        sut.TryGet(uri, out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_on_unknown_uri_does_not_throw()
    {
        var sut = CreateSut();
        var act = () => sut.Remove(MakeUri("/workspace/ghost.feature"));
        act.Should().NotThrow();
    }

    // ── All ───────────────────────────────────────────────────────────────────

    [Fact]
    public void All_returns_all_registered_buffers()
    {
        var sut = CreateSut();
        sut.Update(MakeUri("/workspace/a.feature"), 1, "a");
        sut.Update(MakeUri("/workspace/b.feature"), 2, "b");

        sut.All.Should().HaveCount(2);
    }

    [Fact]
    public void All_is_empty_when_no_buffers_registered()
    {
        var sut = CreateSut();
        sut.All.Should().BeEmpty();
    }

    // ── Case-insensitive drive letter ──────────────────────────────────────────

    [Fact]
    public void Update_with_upper_case_drive_is_retrievable_with_lower_case_drive()
    {
        var sut = CreateSut();
        var upper = DocumentUri.FromFileSystemPath("C:\\workspace\\test.feature");
        var lower = DocumentUri.FromFileSystemPath("c:\\workspace\\test.feature");

        sut.Update(upper, 1, "Feature: X\n");
        sut.TryGet(lower, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be("Feature: X\n");

        // Also works in reverse
        sut.Update(lower, 2, "Feature: Y\n");
        sut.TryGet(upper, out var buffer2).Should().BeTrue();
        buffer2!.Text.Should().Be("Feature: Y\n");
    }

    [Fact]
    public void Remove_with_different_case_still_removes()
    {
        var sut = CreateSut();
        var upper = DocumentUri.FromFileSystemPath("C:\\workspace\\test.feature");
        var lower = DocumentUri.FromFileSystemPath("c:\\workspace\\test.feature");

        sut.Update(upper, 1, "text");
        sut.Remove(lower);
        sut.TryGet(upper, out _).Should().BeFalse();
    }
}
