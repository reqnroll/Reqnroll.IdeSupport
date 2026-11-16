using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Documents;

public class DocumentBufferServiceGenerationTests
{
    private static readonly DocumentUri Uri = DocumentUri.FromFileSystemPath("/workspace/gen.feature");

    [Fact]
    public void Update_records_the_generation_on_the_buffer()
    {
        var sut = new DocumentBufferService();

        sut.Update(Uri, 2, "Feature: F\n", generation: 5);

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Generation.Should().Be(5);
    }

    [Fact]
    public void Update_without_a_generation_records_zero()
    {
        var sut = new DocumentBufferService();

        sut.Update(Uri, 2, "Feature: F\n");

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Generation.Should().Be(0);
    }

    [Fact]
    public void UpdateTags_keeps_the_buffers_generation()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 2, "Feature: F\n", generation: 5);

        sut.UpdateTags(Uri, new DerivationStamp(5, 0, 0), Array.Empty<IdeSupportTag>()).Should().BeTrue();

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Generation.Should().Be(5);
    }

    [Fact]
    public void Update_with_an_older_generation_than_the_buffer_holds_is_ignored()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 3, "Feature: Newer\n", generation: 7);

        sut.Update(Uri, 2, "Feature: Older\n", generation: 6);

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be("Feature: Newer\n");
        buffer.Generation.Should().Be(7);
    }

    [Fact]
    public void Update_with_the_same_generation_replaces_the_buffer()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 2, "Feature: A\n", generation: 4);

        sut.Update(Uri, 3, "Feature: B\n", generation: 4);

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be("Feature: B\n");
    }

    [Fact]
    public void Update_after_remove_accepts_any_generation()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 2, "Feature: A\n", generation: 9);
        sut.Remove(Uri);

        sut.Update(Uri, 1, "Feature: Reopened\n", generation: 1);

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Text.Should().Be("Feature: Reopened\n");
    }

    [Fact]
    public void UpdateTags_for_a_superseded_generation_is_rejected_and_leaves_the_newer_buffer_untouched()
    {
        // A parse of generation 5 text finishes after Update replaced the buffer with generation 6.
        var sut = new DocumentBufferService();
        sut.Update(Uri, 2, "Feature: old\n", generation: 5);
        sut.Update(Uri, 3, "Feature: new\n", generation: 6);

        sut.UpdateTags(Uri, new DerivationStamp(5, 0, 0), new List<IdeSupportTag>()).Should().BeFalse();

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Generation.Should().Be(6);
        buffer.Tags.Should().BeNull("the older parse's tags must not be attached to the newer text");
    }

    [Fact]
    public void UpdateTags_for_the_current_generation_after_a_newer_update_is_accepted()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 2, "Feature: old\n", generation: 5);
        sut.Update(Uri, 3, "Feature: new\n", generation: 6);
        var tags = new List<IdeSupportTag>();

        sut.UpdateTags(Uri, new DerivationStamp(6, 0, 0), tags).Should().BeTrue();

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Tags.Should().BeSameAs(tags);
    }

    [Fact]
    public void UpdateTags_keeps_the_stamp_of_the_inputs_the_tags_were_derived_from()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 1, "Feature: F\n", generation: 5);
        var stamp = new DerivationStamp(5, 2, 17);

        sut.UpdateTags(Uri, stamp, new List<IdeSupportTag>()).Should().BeTrue();

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.TagsStamp.Should().Be(stamp);
    }

    [Fact]
    public void Replacing_the_text_discards_the_tags_stamp_with_the_tags()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 1, "Feature: F\n", generation: 5);
        sut.UpdateTags(Uri, new DerivationStamp(5, 2, 17), new List<IdeSupportTag>());

        sut.Update(Uri, 2, "Feature: G\n", generation: 6);

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.Tags.Should().BeNull();
        buffer.TagsStamp.Should().Be(default(DerivationStamp));
    }

    [Fact]
    public void A_stamp_for_superseded_text_leaves_the_buffers_tags_and_stamp_alone()
    {
        var sut = new DocumentBufferService();
        sut.Update(Uri, 1, "Feature: F\n", generation: 6);
        var current = new DerivationStamp(6, 0, 3);
        sut.UpdateTags(Uri, current, new List<IdeSupportTag>());

        sut.UpdateTags(Uri, new DerivationStamp(5, 0, 9), new List<IdeSupportTag>()).Should().BeFalse();

        sut.TryGet(Uri, out var buffer).Should().BeTrue();
        buffer!.TagsStamp.Should().Be(current);
    }
}
