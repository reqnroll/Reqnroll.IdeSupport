using AwesomeAssertions;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class FindUnusedStepDefinitionSteps
{
    private readonly LspScenarioContext _ctx;

    public FindUnusedStepDefinitionSteps(LspScenarioContext ctx) => _ctx = ctx;

    // ── When ───────────────────────────────────────────────────────────────────

    [When("unused step definitions are requested")]
    public async Task WhenUnusedStepDefinitionsAreRequested()
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        _ctx.LastFindUnused = await _ctx.Harness.Client
            .RequestFindUnusedStepDefinitionsAsync()
            .ConfigureAwait(false);
    }

    // ── Then ───────────────────────────────────────────────────────────────────

    [Then(@"(\d+) unused step definition(?:s are|s is| is| are) returned")]
    public void ThenNUnusedStepDefinitionsAreReturned(int expected)
    {
        var count = _ctx.LastFindUnused?.Items?.Count ?? 0;
        count.Should().Be(expected, $"expected {expected} unused step definition(s) but got {count}");
    }

    [Then(@"the unused step definitions include expression ""(.*)""")]
    public void ThenUnusedStepDefinitionsIncludeExpression(string expression)
    {
        _ctx.LastFindUnused.Should().NotBeNull();
        _ctx.LastFindUnused!.Items.Should().Contain(
            item => item.BindingExpression == expression,
            $"an unused step definition with expression '{expression}' should be present");
    }

    /// <summary>
    /// Asserts which project an unused row is credited to. The same binding legitimately appears
    /// in more than one registry — a linked <c>.cs</c>, or a referenced assembly's bindings picked
    /// up by the referencing project's own discovery — and the rows are deduplicated to one, so
    /// which project survives is a deliberate rule (issue #547) rather than enumeration order.
    /// </summary>
    [Then(@"the unused step definition ""([^""]*)"" is attributed to project ""([^""]*)""")]
    public void ThenTheUnusedStepDefinitionIsAttributedToProject(string expression, string projectName)
    {
        _ctx.LastFindUnused.Should().NotBeNull();
        var item = _ctx.LastFindUnused!.Items
            .SingleOrDefault(i => i.BindingExpression == expression);

        item.Should().NotBeNull(
            $"exactly one unused row for '{expression}' should be returned, but the rows were " +
            string.Join("; ", _ctx.LastFindUnused.Items.Select(i => $"{i.ProjectName}:{i.BindingExpression}")));
        item!.ProjectName.Should().Be(projectName);
    }

    [Then(@"the unused step definitions do not include expression ""(.*)""")]
    public void ThenUnusedStepDefinitionsDoNotIncludeExpression(string expression)
    {
        if (_ctx.LastFindUnused is null) return;
        _ctx.LastFindUnused.Items.Should().NotContain(
            item => item.BindingExpression == expression,
            $"expression '{expression}' should not appear in the unused step definitions");
    }
}
