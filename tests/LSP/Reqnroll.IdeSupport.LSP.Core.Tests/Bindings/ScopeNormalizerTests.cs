using Reqnroll.IdeSupport.LSP.Core.TagExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// <see cref="ScopeNormalizer"/> reduces the scopes Reqnroll registers for one binding attribute
/// (alternatives, OR) to a canonical form using only rewrites that cannot change which scenarios
/// match.
/// </summary>
public class ScopeNormalizerTests
{
    private static ScopeSpec Tag(string tag) => new(tag, null, null);
    private static ScopeSpec Feature(string feature) => new(null, feature, null);

    [Fact]
    public void No_scopes_is_unrestricted()
    {
        ScopeNormalizer.Normalize(Array.Empty<ScopeSpec?>()).Should().BeNull();
    }

    [Fact]
    public void A_null_scope_is_unrestricted()
    {
        ScopeNormalizer.Normalize(new ScopeSpec?[] { null }).Should().BeNull();
    }

    [Fact]
    public void An_unrestricted_alternative_makes_the_whole_binding_unrestricted()
    {
        var result = ScopeNormalizer.Normalize(new[] { Tag("@web"), new ScopeSpec(null, null, null) });

        result.Should().BeNull("a bare [Scope] matches every scenario, so no other alternative narrows it");
    }

    [Fact]
    public void A_single_scope_is_kept_as_is()
    {
        var result = ScopeNormalizer.Normalize(new[] { new ScopeSpec("@web", "F", "S") });

        result.Should().Equal(new ScopeSpec("@web", "F", "S"));
    }

    [Fact]
    public void Tag_only_scopes_merge_into_one_or_expression()
    {
        var result = ScopeNormalizer.Normalize(new[] { Tag("@a"), Tag("@b") });

        result.Should().Equal(Tag("(@a) or (@b)"));
    }

    [Fact]
    public void Scopes_with_the_same_titles_merge_their_tags()
    {
        var result = ScopeNormalizer.Normalize(new[] { new ScopeSpec("@a", "F", null), new ScopeSpec("@b", "F", null) });

        result.Should().Equal(new ScopeSpec("(@a) or (@b)", "F", null));
    }

    [Fact]
    public void A_scope_without_a_tag_absorbs_tagged_scopes_with_the_same_titles()
    {
        var result = ScopeNormalizer.Normalize(new[] { new ScopeSpec("@a", "F", null), Feature("F") });

        result.Should().Equal(new[] { Feature("F") }, "F OR (@a AND F) is just F");
    }

    [Fact]
    public void Scopes_restricting_different_things_stay_alternatives()
    {
        var result = ScopeNormalizer.Normalize(new[] { Feature("F"), Tag("@web") });

        result.Should().HaveCount(2).And.Contain(new[] { Feature("F"), Tag("@web") });
    }

    [Fact]
    public void Hook_tags_fold_while_the_scope_stays_separate()
    {
        var result = ScopeNormalizer.Normalize(new[] { Feature("F"), Tag("web"), Tag("smoke") });

        result.Should().HaveCount(2).And.Contain(new[] { Feature("F"), Tag("(@smoke) or (@web)") });
    }

    [Fact]
    public void A_bare_tag_name_gets_its_at_sign_so_it_can_be_combined()
    {
        var result = ScopeNormalizer.Normalize(new[] { Tag("web"), Tag("@smoke") });

        result.Should().Equal(Tag("(@smoke) or (@web)"));
    }

    // A scope tag may be a full tag expression; each member that is OR-ed is parenthesized so the
    // combined text keeps every member as a self-contained group, whatever it contains.
    [Fact]
    public void Full_tag_expressions_are_each_parenthesized_when_they_are_ored_together()
    {
        var result = ScopeNormalizer.Normalize(new[] { Tag("@a and @b"), Tag("@c or @d"), Tag("not @e") });

        result.Should().Equal(Tag("(@a and @b) or (@c or @d) or (not @e)"));
    }

    [Fact]
    public void A_lone_tag_expression_is_not_wrapped()
    {
        ScopeNormalizer.Normalize(new[] { Tag("@a and @b") }).Should().Equal(Tag("@a and @b"));
    }

    [Theory]
    [InlineData(new[] { "@a" }, false)]
    [InlineData(new[] { "@b" }, false)]
    [InlineData(new[] { "@a", "@b" }, true)]
    [InlineData(new[] { "@c" }, true)]
    [InlineData(new[] { "@d" }, false)]
    [InlineData(new string[0], false)]
    public void Ored_tag_expressions_evaluate_as_the_union_of_their_members(string[] scenarioTags, bool expected)
    {
        // (@a and @b) or (@c): a scenario matches when it has both @a and @b, or has @c.
        var scope = ScopeNormalizer.Build(new ScopeSpec?[] { Tag("@a and @b"), Tag("@c") }, new ReqnrollTagExpressionParser());

        scope!.Tag!.Evaluate(scenarioTags).Should().Be(expected);
    }

    [Fact]
    public void Ored_expressions_with_not_evaluate_as_the_union_of_their_members()
    {
        var scope = ScopeNormalizer.Build(new ScopeSpec?[] { Tag("not @a"), Tag("@b") }, new ReqnrollTagExpressionParser());

        scope!.Tag!.Evaluate(new[] { "@a" }).Should().BeFalse();
        scope.Tag.Evaluate(new[] { "@a", "@b" }).Should().BeTrue();
        scope.Tag.Evaluate(new string[0]).Should().BeTrue();
    }

    [Fact]
    public void Duplicate_scopes_collapse()
    {
        var result = ScopeNormalizer.Normalize(new[] { Tag("@a"), Tag("@a"), Feature("F"), Feature("F") });

        result.Should().HaveCount(2).And.Contain(new[] { Tag("@a"), Feature("F") });
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_scopes()
    {
        var scopes = new[] { Tag("@b"), Feature("F"), Tag("@a"), new ScopeSpec(null, null, "S"), Tag("@c") };

        var forward = ScopeNormalizer.Normalize(scopes);
        var backward = ScopeNormalizer.Normalize(scopes.Reverse().ToArray());

        backward.Should().Equal(forward);
    }

    [Fact]
    public void A_scope_reported_with_an_error_is_never_merged_or_absorbed()
    {
        var broken = new ScopeSpec("@a", "F", null, "boom");

        var result = ScopeNormalizer.Normalize(new[] { Feature("F"), broken });

        result.Should().Contain(broken);
        result.Should().Contain(Feature("F"));
    }

    [Fact]
    public void An_error_only_scope_is_not_treated_as_unrestricted()
    {
        var result = ScopeNormalizer.Normalize(new[] { new ScopeSpec(null, null, null, "boom") });

        result.Should().NotBeNull();
    }

    [Fact]
    public void ToBindingScope_gives_a_single_scope_for_one_entry_and_alternatives_for_several()
    {
        var parser = new ReqnrollTagExpressionParser();

        var single = ScopeNormalizer.Build(new ScopeSpec?[] { Tag("@a"), Tag("@b") }, parser);
        var several = ScopeNormalizer.Build(new ScopeSpec?[] { Tag("@a"), Feature("F") }, parser);

        single!.Alternatives.Should().BeNull();
        single.Tag.Should().NotBeNull();
        several!.Alternatives.Should().HaveCount(2);
        several.Tag.Should().BeNull();
    }

    [Fact]
    public void An_invalid_tag_expression_becomes_the_scopes_error()
    {
        var scope = ScopeNormalizer.Build(new ScopeSpec?[] { Tag("@a and"), Feature("F") }, new ReqnrollTagExpressionParser());

        scope!.IsValid.Should().BeFalse();
        scope.Error.Should().Contain("Invalid tag expression '@a and'");
    }
}
