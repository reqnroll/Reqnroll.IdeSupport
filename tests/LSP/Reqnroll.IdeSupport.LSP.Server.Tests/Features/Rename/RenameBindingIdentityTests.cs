using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TagExpressions;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

/// <summary>
/// Issue #671 (R5): a disambiguation session must name <i>which</i> binding the user picked, not
/// its position in a list that gets rebuilt before the rename runs.
/// </summary>
public class RenameBindingIdentityTests
{
    private static ProjectStepDefinitionBinding MakeBinding(
        ScenarioBlock type = ScenarioBlock.Given,
        string expression = "the first number is {int}",
        string method = "N.Steps.GivenTheFirstNumberIs(Int32)",
        string? scopeTag = null)
    {
        var scope = scopeTag is null
            ? null
            : new BindingScope { Tag = ReqnrollTagExpressionParser.CreateTagLiteral(scopeTag) };
        var implementation = new ProjectBindingImplementation(
            method, null, new SourceLocation("/workspace/Steps.cs", 8, 9));
        return new ProjectStepDefinitionBinding(
            type, new Regex($"^{Regex.Escape(expression)}$"), scope!, implementation, expression);
    }

    // ── The key discriminates on what actually differs in each flow ──────────

    [Fact]
    public void Bindings_differing_only_by_implementing_method_get_different_keys()
    {
        // The .feature ambiguous case: identical step text bound to different methods is exactly
        // what makes those candidates ambiguous, so the method has to be part of the key.
        var a = MakeBinding(method: "N.Steps.GivenA()");
        var b = MakeBinding(method: "N.OtherSteps.GivenA()");

        RenameBindingIdentity.For(a).Should().NotBe(RenameBindingIdentity.For(b));
    }

    [Fact]
    public void Bindings_differing_only_by_step_type_get_different_keys()
    {
        // The .cs multi-attribute case: [Given("x")] and [When("x")] on one method.
        var given = MakeBinding(ScenarioBlock.Given, expression: "x");
        var when  = MakeBinding(ScenarioBlock.When, expression: "x");

        RenameBindingIdentity.For(given).Should().NotBe(RenameBindingIdentity.For(when));
    }

    [Fact]
    public void Bindings_differing_only_by_expression_get_different_keys()
    {
        var a = MakeBinding(expression: "the first number is {int}");
        var b = MakeBinding(expression: "the second number is {int}");

        RenameBindingIdentity.For(a).Should().NotBe(RenameBindingIdentity.For(b));
    }

    [Fact]
    public void Bindings_differing_only_by_scope_get_different_keys()
    {
        var unscoped = MakeBinding();
        var scoped   = MakeBinding(scopeTag: "web");

        RenameBindingIdentity.For(unscoped).Should().NotBe(RenameBindingIdentity.For(scoped));
    }

    [Fact]
    public void Two_separately_constructed_but_equivalent_bindings_get_the_same_key()
    {
        // The key has to survive the registry being rebuilt between selectRenameTarget and
        // rename — it is derived from values, never from object identity.
        RenameBindingIdentity.For(MakeBinding()).Should().Be(RenameBindingIdentity.For(MakeBinding()));
    }

    // ── Finding the picked binding again ─────────────────────────────────────

    [Fact]
    public void FindIn_locates_the_picked_binding_after_the_candidate_order_changes()
    {
        // The reason an index is unsafe: FindBindingsAtFeatureStep returns a HashSet's ToList(),
        // whose order depends on an insertion sequence a reparse can change.
        var first  = MakeBinding(method: "N.Steps.GivenA()");
        var second = MakeBinding(method: "N.OtherSteps.GivenA()");
        var identity = RenameBindingIdentity.For(second);

        var reordered = new[] { second, first };

        RenameBindingIdentity.FindIn(reordered, identity).Should().BeSameAs(second);
    }

    [Fact]
    public void FindIn_returns_null_when_the_picked_binding_is_gone()
    {
        var picked = MakeBinding(method: "N.Steps.GivenA()");
        var identity = RenameBindingIdentity.For(picked);

        var remaining = new[] { MakeBinding(method: "N.OtherSteps.GivenA()") };

        RenameBindingIdentity.FindIn(remaining, identity).Should().BeNull();
    }

    [Fact]
    public void FindIn_returns_null_for_an_empty_candidate_list()
    {
        RenameBindingIdentity.FindIn(Array.Empty<ProjectStepDefinitionBinding>(), "anything")
            .Should().BeNull();
    }

    [Fact]
    public void FindIn_returns_null_rather_than_guessing_between_indistinguishable_bindings()
    {
        // Two genuinely identical attributes on one method — e.g. [Given("x")] twice. There is
        // nothing to choose between them, and picking one arbitrarily is the failure this exists
        // to prevent, so the caller is told the session is unusable instead.
        var duplicate = MakeBinding();
        var identity = RenameBindingIdentity.For(duplicate);

        RenameBindingIdentity.FindIn(new[] { MakeBinding(), MakeBinding() }, identity)
            .Should().BeNull();
    }
}
