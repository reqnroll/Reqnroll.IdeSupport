#nullable enable

using System;
using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

public class RenameSessionManagerTests
{
    private static RenameSessionManager CreateSut() => new();

    private sealed class FakeClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => UtcNow;
    }

    [Fact]
    public void SetSession_then_TryConsume_returns_correct_attributeIndex()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 3);
        var consumed = sut.TryConsume("test.cs", 1, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(3);
    }

    [Fact]
    public void TryConsume_without_set_returns_false()
    {
        var sut = CreateSut();

        var consumed = sut.TryConsume("test.cs", 1, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void TryConsume_wrong_version_returns_false()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 3);
        var consumed = sut.TryConsume("test.cs", 2, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void TryConsume_consumed_only_once()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 7);
        var first = sut.TryConsume("test.cs", 1, out var firstIndex);
        var second = sut.TryConsume("test.cs", 1, out var secondIndex);

        first.Should().BeTrue();
        firstIndex.Should().Be(7);
        second.Should().BeFalse();
    }

    [Fact]
    public void Multiple_sessions_independent()
    {
        var sut = CreateSut();

        sut.SetSession("file-a.cs", 1, 10);
        sut.SetSession("file-b.cs", 2, 20);

        var consumedA = sut.TryConsume("file-a.cs", 1, out var indexA);
        var consumedB = sut.TryConsume("file-b.cs", 2, out var indexB);

        consumedA.Should().BeTrue();
        indexA.Should().Be(10);
        consumedB.Should().BeTrue();
        indexB.Should().Be(20);
    }

    [Fact]
    public void TryConsume_immediately_after_set_succeeds()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 5);
        var consumed = sut.TryConsume("test.cs", 1, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(5);
    }

    [Fact]
    public void TryConsume_just_before_expiry_succeeds()
    {
        var clock = new FakeClock();
        var sut = new RenameSessionManager(clock.Now);

        sut.SetSession("test.cs", 1, 5);
        clock.UtcNow = clock.UtcNow.AddSeconds(30).AddMilliseconds(-1);
        var consumed = sut.TryConsume("test.cs", 1, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(5);
    }

    [Fact]
    public void TryConsume_after_30_second_expiry_returns_false()
    {
        var clock = new FakeClock();
        var sut = new RenameSessionManager(clock.Now);

        sut.SetSession("test.cs", 1, 5);
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        var consumed = sut.TryConsume("test.cs", 1, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Cleanup_evicts_expired_sessions_so_a_later_SetSession_does_not_see_them()
    {
        var clock = new FakeClock();
        var sut = new RenameSessionManager(clock.Now);

        sut.SetSession("file-a.cs", 1, 5);
        clock.UtcNow = clock.UtcNow.AddSeconds(31);

        // Cleanup() runs as a side effect of SetSession/TryConsume; setting a second,
        // unrelated session should sweep the expired first one so it can never be consumed,
        // even though TryConsume itself would also independently reject it.
        sut.SetSession("file-b.cs", 2, 9);

        var consumedExpired = sut.TryConsume("file-a.cs", 1, out _);
        var consumedFresh = sut.TryConsume("file-b.cs", 2, out var freshIndex);

        consumedExpired.Should().BeFalse();
        consumedFresh.Should().BeTrue();
        freshIndex.Should().Be(9);
    }

    [Fact]
    public void SetSession_with_version_zero_is_consumable_with_version_zero()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 0, 2);
        var consumed = sut.TryConsume("test.cs", 0, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(2);
    }

    [Fact]
    public void TryConsume_with_different_uri_returns_false()
    {
        var sut = CreateSut();

        sut.SetSession("file-a.cs", 1, 10);
        var consumed = sut.TryConsume("file-b.cs", 1, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void SetSession_with_upper_case_drive_is_consumable_with_lower_case_drive()
    {
        var sut = CreateSut();

        sut.SetSession("file:///C:/Users/test/Steps.cs", 0, 2);
        var consumed = sut.TryConsume("file:///c:/Users/test/Steps.cs", 0, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(2);
    }

    // ── Regression: VS Code's Uri.toString() percent-encodes the Windows drive-letter colon
    //    (file:///c%3A/...), which reqnroll/selectRenameTarget carries verbatim as a raw string,
    //    while the subsequent textDocument/rename request's URI is round-tripped server-side
    //    through OmniSharp's DocumentUri and comes back unescaped (file:///c:/...). Before this
    //    fix, TryConsume's key never matched SetSession's for real client traffic, so the
    //    session was always "missed" and HandleRenameAsync silently fell back to picking the
    //    first ambiguous candidate — regardless of which one the user actually selected in the
    //    picker. ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetSession_with_percent_encoded_drive_colon_is_consumable_with_plain_colon()
    {
        var sut = CreateSut();

        sut.SetSession("file:///c%3A/Users/test/Calculator.feature", 0, 1);
        var consumed = sut.TryConsume("file:///c:/Users/test/Calculator.feature", 0, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(1);
    }

    [Fact]
    public void TryConsume_with_percent_encoded_drive_colon_is_consumable_with_session_set_plain()
    {
        var sut = CreateSut();

        sut.SetSession("file:///c:/Users/test/Calculator.feature", 0, 1);
        var consumed = sut.TryConsume("file:///c%3A/Users/test/Calculator.feature", 0, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(1);
    }

    /// <summary>
    /// Multiple bindings in the same file can share the same expression with different
    /// [Scope] attributes. The session stores the picker's selection (attributeIndex)
    /// so that HandleRenameAsync can identify which binding was chosen. This test
    /// verifies that consecutive SetSession calls with different attributeIndex values
    /// are each consumed correctly — simulating the user selecting different scoped
    /// duplicates in the picker across separate rename invocations.
    /// </summary>
    [Fact]
    public void Multiple_sessions_same_uri_different_attributeIndex()
    {
        var sut = CreateSut();

        // First rename — select attributeIndex 0
        sut.SetSession("file:///project/Steps.cs", 0, 0);
        var first = sut.TryConsume("file:///project/Steps.cs", 0, out var firstIndex);

        first.Should().BeTrue();
        firstIndex.Should().Be(0);

        // Second rename — select attributeIndex 2 (third binding with same expression)
        sut.SetSession("file:///project/Steps.cs", 0, 2);
        var second = sut.TryConsume("file:///project/Steps.cs", 0, out var secondIndex);

        second.Should().BeTrue();
        secondIndex.Should().Be(2);
    }

    [Fact]
    public void SetSession_with_malformed_percent_encoding_does_not_throw()
    {
        var sut = CreateSut();

        // "%ZZ" is not a valid percent-encoded sequence; Uri.UnescapeDataString throws for it,
        // which NormalizeUri's catch-all falls back from by using the raw (still-escaped) value.
        var act = () => sut.SetSession("file:///project/%ZZ/Steps.cs", 0, 1);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryConsume_with_malformed_percent_encoding_does_not_throw_and_is_consumable_with_matching_raw_uri()
    {
        var sut = CreateSut();

        sut.SetSession("file:///project/%ZZ/Steps.cs", 0, 1);
        var consumed = sut.TryConsume("file:///project/%ZZ/Steps.cs", 0, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(1);
    }
}
