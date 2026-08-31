namespace Reqnroll.IdeSupport.LSP.Core.Tests.Documents;

/// <summary>
/// Covers the value semantics added for issue #540 F6, and the resolution state added for F1.
/// </summary>
public class SourceLocationTests
{
    // ── Value equality (F6) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Two_locations_with_the_same_file_and_position_are_equal()
    {
        // Before #540 this was reference equality, which silently defeated
        // ProjectBindingImplementationEqualityComparer's own promise to compare structurally.
        var a = new SourceLocation(@"C:\Repo\Steps.cs", 12, 5, 12, 20);
        var b = new SourceLocation(@"C:\Repo\Steps.cs", 12, 5, 12, 20);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_uses_the_shared_path_comparison()
    {
        // The two discovery paths disagree on separators and drive-letter casing for the same file.
        var fromPdb = new SourceLocation(@"C:\Repo\Steps.cs", 12, 5);
        var fromUri = new SourceLocation("c:/Repo/Steps.cs", 12, 5);

        fromPdb.Should().Be(fromUri);
        fromPdb.GetHashCode().Should().Be(fromUri.GetHashCode());
    }

    [Fact]
    public void Locations_differing_only_in_position_are_not_equal()
    {
        new SourceLocation(@"C:\Repo\Steps.cs", 12, 5)
            .Should().NotBe(new SourceLocation(@"C:\Repo\Steps.cs", 13, 5));
    }

    [Fact]
    public void Locations_differing_only_in_end_position_are_not_equal()
    {
        new SourceLocation(@"C:\Repo\Steps.cs", 12, 5, 12, 20)
            .Should().NotBe(new SourceLocation(@"C:\Repo\Steps.cs", 12, 5));
    }

    [Fact]
    public void Locations_in_different_files_are_not_equal()
    {
        new SourceLocation(@"C:\Repo\Steps.cs", 12, 5)
            .Should().NotBe(new SourceLocation(@"C:\Repo\Other.cs", 12, 5));
    }

    [Fact]
    public void Equality_is_reflexive_for_a_location_with_no_file()
    {
        // PathUtils.IsSamePath answers false for an absent path on both sides — the right answer
        // when deciding document ownership, the wrong one for Equals, which must stay reflexive.
        var a = new SourceLocation(null!, 1, 1);
        var b = new SourceLocation(null!, 1, 1);

        a.Should().Be(a);
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void A_location_with_no_file_is_not_equal_to_one_with_a_file()
    {
        new SourceLocation(null!, 1, 1).Should().NotBe(new SourceLocation(@"C:\Repo\Steps.cs", 1, 1));
    }

    [Fact]
    public void A_location_is_not_equal_to_a_non_location()
    {
        new SourceLocation(@"C:\Repo\Steps.cs", 1, 1).Equals("not a location").Should().BeFalse();
        new SourceLocation(@"C:\Repo\Steps.cs", 1, 1).Equals(null).Should().BeFalse();
    }

    // ── Resolution state (F1) ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_location_built_through_the_constructor_is_resolved()
    {
        // Roslyn/source-level discovery builds locations from live LSP document URIs, which are
        // local by construction — the flag must not force every existing call site to opt in.
        var loc = new SourceLocation(@"C:\Repo\Steps.cs", 4, 1);

        loc.IsResolved.Should().BeTrue();
        loc.RecordedSourceFile.Should().Be(@"C:\Repo\Steps.cs");
    }

    [Fact]
    public void An_unresolved_location_keeps_the_recorded_path_for_diagnostics()
    {
        var loc = SourceLocation.Unresolved("/workspaces/host-solution/Support/Hooks.cs", 9, 3);

        loc.IsResolved.Should().BeFalse();
        loc.RecordedSourceFile.Should().Be("/workspaces/host-solution/Support/Hooks.cs");
        loc.SourceFile.Should().Be("/workspaces/host-solution/Support/Hooks.cs");
    }

    [Fact]
    public void WithPosition_preserves_resolution_state_and_both_paths()
    {
        // The method-identifier backfill rewrites the position; rebuilding through the public
        // constructor instead would silently re-assert IsResolved.
        var loc = SourceLocation.Unresolved("/workspaces/host/Hooks.cs", 9, 3, 9, 3)
            .WithPosition(7, 17);

        loc.IsResolved.Should().BeFalse();
        loc.SourceFileLine.Should().Be(7);
        loc.SourceFileColumn.Should().Be(17);
        loc.RecordedSourceFile.Should().Be("/workspaces/host/Hooks.cs");
    }

    [Fact]
    public void WithPosition_on_a_resolved_location_stays_resolved()
    {
        var loc = new SourceLocation(@"C:\Repo\Steps.cs", 9, 3).WithPosition(7, 17, 7, 30);

        loc.IsResolved.Should().BeTrue();
        loc.SourceFileEndLine.Should().Be(7);
        loc.SourceFileEndColumn.Should().Be(30);
    }
}
