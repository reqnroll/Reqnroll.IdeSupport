using System.Collections.Generic;
using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.Classification;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.Classification;

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
        store.TokensChanged += k => changedKey = k;

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
        store.TokensChanged += _ => changedCount++;

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
}
