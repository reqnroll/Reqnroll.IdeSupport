using System.Collections.Generic;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.Classification;

public class SemanticTokenClassificationStoreTests
{
    // ── NormalizeKey ────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeKey_treats_a_file_uri_and_the_equivalent_local_path_as_the_same_key()
    {
        // The interceptor sees a file:// URI; the classifier sees ITextDocument.FilePath.
        // Both must normalize to the same key (incl. drive-letter case) or nothing colours.
        var fromUri = SemanticTokenClassificationStore.NormalizeKey("file:///C:/Users/x/Features/A.feature");
        var fromPath = SemanticTokenClassificationStore.NormalizeKey(@"c:\Users\x\Features\A.feature");

        fromUri.Should().NotBeNull();
        fromUri.Should().Be(fromPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeKey_returns_null_for_null_or_empty(string? input)
        => SemanticTokenClassificationStore.NormalizeKey(input).Should().BeNull();

    // ── Set / TryGet / event ────────────────────────────────────────────────────

    [Fact]
    public void SetTokens_round_trips_through_TryGetTokens_and_raises_TokensChanged()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        string? changedKey = null;
        store.TokensChanged += (k, _, _) => changedKey = k;

        store.SetTokens(key, new List<ClassifiedToken> { new(0, 0, 5, "reqnroll.keyword") });

        changedKey.Should().Be(key, "the classifier subscribes to TokensChanged to trigger a recolor");
        store.TryGetTokens(key, out var got).Should().BeTrue();
        got.Should().ContainSingle();
        got[0].TokenType.Should().Be("reqnroll.keyword");
        got[0].Length.Should().Be(5);
    }

    [Fact]
    public void TryGetTokens_returns_false_for_an_unknown_key()
    {
        var store = new SemanticTokenClassificationStore();

        store.TryGetTokens("nope", out var got).Should().BeFalse();
        got.Should().BeEmpty();
    }

    [Fact]
    public void SetTokens_called_again_for_the_same_key_overwrites_rather_than_duplicates()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        var changedCount = 0;
        store.TokensChanged += (_, _, _) => changedCount++;

        store.SetTokens(key, new List<ClassifiedToken> { new(0, 0, 5, "reqnroll.keyword") });
        store.SetTokens(key, new List<ClassifiedToken> { new(1, 0, 3, "reqnroll.tag") });

        store.TryGetTokens(key, out var got).Should().BeTrue();
        got.Should().ContainSingle("the second SetTokens call should replace, not append to, the first");
        got[0].TokenType.Should().Be("reqnroll.tag");
        changedCount.Should().Be(2, "TokensChanged should fire for every SetTokens call, not just the first");
    }

    [Fact]
    public void SetTokens_is_safe_under_concurrent_writes_across_many_keys()
    {
        // _byFile is a ConcurrentDictionary shared between the LSP interceptor (writer) and the
        // editor classifier (reader); this drives real concurrent writes/reads across many
        // distinct keys to confirm no torn state or lost update under contention.
        var store = new SemanticTokenClassificationStore();
        const int keyCount = 50;
        var keys = Enumerable.Range(0, keyCount).Select(i => $"c:\\w\\F{i}.feature").ToArray();

        Parallel.ForEach(keys, key =>
        {
            for (var i = 0; i < 20; i++)
            {
                store.SetTokens(key, new List<ClassifiedToken> { new(i, 0, 1, "reqnroll.keyword") });
                store.TryGetTokens(key, out _);
            }
        });

        foreach (var key in keys)
        {
            store.TryGetTokens(key, out var got).Should().BeTrue();
            got.Should().ContainSingle();
        }
    }

    // ── ApplyRange ──────────────────────────────────────────────────────────────

    private static ClassifiedToken T(int line, int ch, string type = "reqnroll.keyword") => new(line, ch, 3, type);

    [Fact]
    public void ApplyRange_replaces_only_the_tokens_on_the_given_lines()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        store.SetTokens(key, new List<ClassifiedToken> { T(0, 0), T(1, 0), T(1, 5), T(2, 0), T(3, 0) });

        var applied = store.ApplyRange(key, 1, 2, new List<ClassifiedToken> { T(1, 2, "reqnroll.tag") });

        applied.Should().BeTrue();
        store.TryGetTokens(key, out var got).Should().BeTrue();
        got.Select(t => (t.Line, t.StartChar, t.TokenType)).Should().Equal(
            (0, 0, "reqnroll.keyword"),
            (1, 2, "reqnroll.tag"),
            (3, 0, "reqnroll.keyword"));
    }

    [Fact]
    public void ApplyRange_with_no_replacement_tokens_clears_those_lines()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        store.SetTokens(key, new List<ClassifiedToken> { T(0, 0), T(1, 0), T(2, 0) });

        store.ApplyRange(key, 2, 2, new List<ClassifiedToken>()).Should().BeTrue();

        store.TryGetTokens(key, out var got);
        got.Select(t => t.Line).Should().Equal(0, 1);
    }

    [Fact]
    public void ApplyRange_raises_TokensChanged_for_just_that_line_range()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        store.SetTokens(key, new List<ClassifiedToken> { T(0, 0), T(5, 0) });
        var raised = new List<(string Key, int Start, int End)>();
        store.TokensChanged += (k, s, e) => raised.Add((k, s, e));

        store.ApplyRange(key, 4, 6, new List<ClassifiedToken> { T(5, 1) });
        store.SetTokens(key, new List<ClassifiedToken> { T(0, 0) });

        raised.Should().Equal((key, 4, 6), (key, -1, -1));
    }

    [Fact]
    public void ApplyRange_changes_nothing_and_returns_false_when_the_file_has_no_complete_set_yet()
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        var raised = 0;
        store.TokensChanged += (_, _, _) => raised++;

        var applied = store.ApplyRange(key, 0, 0, new List<ClassifiedToken> { T(0, 0) });

        applied.Should().BeFalse();
        store.TryGetTokens(key, out _).Should().BeFalse("a range alone is not a complete token set");
        raised.Should().Be(0);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(-1, 2)]
    [InlineData(-5, -1)]
    public void ApplyRange_rejects_an_inverted_or_negative_line_range(int startLine, int endLine)
    {
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        store.SetTokens(key, new List<ClassifiedToken> { T(0, 0) });

        store.ApplyRange(key, startLine, endLine, new List<ClassifiedToken>()).Should().BeFalse();

        store.TryGetTokens(key, out var got);
        got.Should().ContainSingle();
    }

    [Fact]
    public void ApplyRange_does_not_lose_updates_under_concurrent_writes()
    {
        // Each writer owns one line and replaces its tokens many times; whichever order the updates interleave
        // in, every line must end up with its writer's last value and no line may be lost or duplicated.
        var store = new SemanticTokenClassificationStore();
        var key = SemanticTokenClassificationStore.NormalizeKey(@"c:\w\A.feature")!;
        const int lines = 16;
        store.SetTokens(key, Enumerable.Range(0, lines).Select(l => T(l, 0)).ToList());

        Parallel.For(0, lines, line =>
        {
            for (var i = 1; i <= 50; i++)
                store.ApplyRange(key, line, line, new List<ClassifiedToken> { new(line, 0, i, "reqnroll.keyword") });
        });

        store.TryGetTokens(key, out var got);
        got.Select(t => t.Line).Should().Equal(Enumerable.Range(0, lines));
        got.Select(t => t.Length).Should().AllBeEquivalentTo(50);
    }
}
