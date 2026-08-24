using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefs;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.FindUnusedStepDefs;

public class FindUnusedStepDefinitionsServiceTests
{
    private readonly IBindingMatchService _matchService = Substitute.For<IBindingMatchService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private FindUnusedStepDefinitionsService CreateSut() => new(_matchService, _logger);

    // ── Helper factory methods ─────────────────────────────────────────────────

    private static ProjectStepDefinitionBinding MakeBinding(
        string sourceFile,
        int line          = 1,
        int column        = 1,
        string method     = "StepDefinitions.GivenSomething()",
        string expression = "something")
    {
        var loc  = new SourceLocation(sourceFile, line, column);
        var impl = new ProjectBindingImplementation(method, null, loc);
        return new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex("^something$"),
            null,
            impl,
            expression);
    }

    /// <summary>
    /// Returns two bindings on the <em>same</em> C# method (shared
    /// <see cref="ProjectBindingImplementation"/>, same source location), mirroring what
    /// the connector produces when a method carries multiple step-attribute decorations:
    /// <code>
    /// [Given("first expression")]
    /// [When("second expression")]
    /// public void MyMethod() { … }
    /// </code>
    /// Both <see cref="ProjectStepDefinitionBinding"/> objects reference the same
    /// <see cref="ProjectBindingImplementation"/> instance, so they share
    /// <see cref="SourceLocation"/>, class name, and method name — but each still gets its own
    /// <see cref="BindingId"/> (identity includes <c>Expression</c>, issue #471).
    /// </summary>
    private static (ProjectStepDefinitionBinding First, ProjectStepDefinitionBinding Second)
        MakeTwoExpressionsOnSameMethod(
            string sourceFile,
            int    line        = 10,
            string method      = "StepDefs.MultiAttributeMethod()",
            string expression1 = "first expression",
            string expression2 = "second expression")
    {
        var loc  = new SourceLocation(sourceFile, line, 1);
        var impl = new ProjectBindingImplementation(method, null, loc);  // shared instance

        var b1 = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex("^" + Regex.Escape(expression1) + "$"),
            null, impl, expression1);

        var b2 = new ProjectStepDefinitionBinding(
            ScenarioBlock.When,
            new Regex("^" + Regex.Escape(expression2) + "$"),
            null, impl, expression2);

        return (b1, b2);
    }

    /// <summary>Returns a <see cref="StepBindingMatch"/> whose <c>Result.Items</c> record <paramref name="binding"/> as the matched step definition.</summary>
    private static StepBindingMatch MakeMatchForBinding(ProjectStepDefinitionBinding binding)
    {
        var snapshot = new LspTextSnapshot(
            "file:///any.feature", 1,
            "Feature: F\nScenario: S\n    Given x\n");
        var range  = GherkinRange.FromPoint(snapshot, 33, 1);
        var item   = MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch);
        var result = MatchResult.CreateMultiMatch(new[] { item });
        return new StepBindingMatch("file:///any.feature", range, result);
    }

    private static (string ProjectName, ProjectBindingRegistry Registry)
        MakeEntry(string projectName, params ProjectStepDefinitionBinding[] bindings) =>
        (projectName, ProjectBindingRegistry.FromBindings(bindings));

    /// <summary>Stubs <see cref="IBindingMatchService.FindUsages(BindingId,IReadOnlyCollection{ProjectOwner})"/> for the given binding's identity to return <paramref name="usages"/>.</summary>
    private void StubUsagesFor(ProjectStepDefinitionBinding binding, params StepBindingMatch[] usages)
    {
        var id = BindingId.For(binding);
        _matchService.FindUsages(id, Arg.Any<IReadOnlyCollection<ProjectOwner>>()).Returns(usages);
    }

    // ── Empty workspace ────────────────────────────────────────────────────────

    [Fact]
    public void No_projects_returns_empty_result()
    {
        var result = CreateSut().FindUnusedStepDefinitions(Array.Empty<(string, ProjectBindingRegistry)>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void Project_with_no_bindings_returns_empty_result()
    {
        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A") });

        result.Should().BeEmpty();
    }

    // ── Unused detection ──────────────────────────────────────────────────────

    [Fact]
    public void Binding_with_no_usages_is_reported()
    {
        // NSubstitute returns Array.Empty<StepBindingMatch> for any un-stubbed FindUsages call.
        var binding = MakeBinding("/ws/Steps.cs", line: 10);

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Should().ContainSingle();
    }

    [Fact]
    public void Binding_with_usages_is_not_reported()
    {
        var binding = MakeBinding("/ws/Steps.cs", line: 10);
        StubUsagesFor(binding, MakeMatchForBinding(binding));

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Should().BeEmpty();
    }

    // ── Result fields ────────────────────────────────────────────────────────

    [Fact]
    public void Reports_project_name_from_registry_entry()
    {
        var binding = MakeBinding("/ws/Steps.cs");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("MyProject", binding) });

        result.Single().ProjectName.Should().Be("MyProject");
    }

    [Fact]
    public void Reports_class_name_parsed_from_method()
    {
        var binding = MakeBinding("/ws/Steps.cs", method: "StepDefs.GivenSomething()");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Single().ClassName.Should().Be("StepDefs");
    }

    [Fact]
    public void Reports_method_name_parsed_from_method_without_params()
    {
        var binding = MakeBinding("/ws/Steps.cs", method: "StepDefs.GivenSomething(int, string)");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Single().MethodName.Should().Be("GivenSomething");
    }

    [Fact]
    public void Reports_method_name_from_namespaced_roslyn_method()
    {
        // Roslyn path produces "Namespace.ClassName.MethodName" (no params)
        var binding = MakeBinding("/ws/Steps.cs", method: "MyApp.Steps.GivenSomething");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        var item = result.Single();
        item.ClassName.Should().Be("Steps");
        item.MethodName.Should().Be("GivenSomething");
    }

    [Fact]
    public void Reports_binding_expression()
    {
        var binding = MakeBinding("/ws/Steps.cs", expression: "the sum is {int}");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Single().BindingExpression.Should().Be("the sum is {int}");
    }

    [Fact]
    public void Reports_source_file_from_source_location()
    {
        var binding = MakeBinding("/ws/MySteps.cs", line: 42);

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Single().SourceFile.Should().Be("/ws/MySteps.cs");
    }

    [Fact]
    public void Reports_1based_source_line_and_column_unchanged()
    {
        // 1-based → 0-based wire conversion is the Server handler's responsibility, not the
        // Core service's - the service returns the domain-native 1-based position.
        var binding = MakeBinding("/ws/Steps.cs", line: 10, column: 5);

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Single().SourceLine.Should().Be(10);
        result.Single().SourceColumn.Should().Be(5);
    }

    // ── Deduplication (same binding identity in multiple project registries) ───

    [Fact]
    public void Deduplicates_same_binding_across_projects()
    {
        var binding = MakeBinding("/ws/Steps.cs", line: 10);
        var entryA  = MakeEntry("A", binding);
        var entryB  = MakeEntry("B", binding);

        var result = CreateSut().FindUnusedStepDefinitions(new[] { entryA, entryB });

        // Same BindingId (same declaring type/method/params/block/expression) → reported once.
        result.Should().ContainSingle();
    }

    [Fact]
    public void Reports_distinct_bindings_in_same_project()
    {
        var b1 = MakeBinding("/ws/Steps.cs", line: 10);
        var b2 = MakeBinding("/ws/Steps.cs", line: 20, method: "StepDefinitions.GivenSomethingElse()");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", b1, b2) });

        result.Should().HaveCount(2);
    }

    // ── FindUsages is called with no project filter (global intersection) ──────

    [Fact]
    public void Passes_null_project_filter_to_FindUsages()
    {
        var binding = MakeBinding("/ws/Steps.cs");

        CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        _matchService.Received(1).FindUsages(
            BindingId.For(binding),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f => f == null));
    }

    // ── Invalid bindings are skipped ──────────────────────────────────────────

    [Fact]
    public void Skips_bindings_with_no_source_location()
    {
        var impl    = new ProjectBindingImplementation("MyClass.MyMethod()", null, null!);
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex("^x$"),
            null, impl, "x");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Should().BeEmpty();
        _matchService.DidNotReceive()
                     .FindUsages(Arg.Any<BindingId>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    [Fact]
    public void Skips_invalid_bindings_regex_null()
    {
        var loc     = new SourceLocation("/ws/Steps.cs", 1, 1);
        var impl    = new ProjectBindingImplementation("MyClass.MyMethod()", null, loc);
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            null,    // null regex → IsValid == false
            null, impl, "x");

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", binding) });

        result.Should().BeEmpty();
        _matchService.DidNotReceive()
                     .FindUsages(Arg.Any<BindingId>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    // ── Invalid registry is skipped ───────────────────────────────────────────

    [Fact]
    public void Skips_invalid_registry()
    {
        var result = CreateSut().FindUnusedStepDefinitions(
            new[] { ("A", ProjectBindingRegistry.Invalid) });

        result.Should().BeEmpty();
    }

    // ── Multiple binding attributes on the same method ────────────────────────
    //
    // A C# method may carry more than one step attribute:
    //
    //   [Given("first expression")]
    //   [When("second expression")]
    //   public void MultiAttributeMethod() { … }
    //
    // The connector produces one ProjectStepDefinitionBinding per attribute, sharing the SAME
    // ProjectBindingImplementation (same source file and line) but each with its own BindingId
    // (identity includes StepDefinitionType + Expression, issue #471) — so each expression gets
    // its own precise FindUsages lookup, with no need for a per-location cache or a post-hoc
    // "does this usage's Expression match?" filter.

    [Fact]
    public void Method_with_two_expressions_both_unused_reports_two_rows()
    {
        var (b1, b2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 10,
            expression1: "first expression",
            expression2: "second expression");
        // Neither expression stubbed → both resolve to "no usages".

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", b1, b2) });

        result.Should().HaveCount(2, "each unused expression is a separate result row");
        result.Select(i => i.BindingExpression)
              .Should().BeEquivalentTo(new[] { "first expression", "second expression" });
    }

    [Fact]
    public void Method_with_two_expressions_calls_FindUsages_once_per_expression()
    {
        // Two distinct expressions on one method → two distinct BindingIds → two lookups, each
        // an O(1) reverse-index hit (issue #471) rather than one location scan reused twice.
        var (b1, b2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 10);

        CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", b1, b2) });

        _matchService.Received(1).FindUsages(BindingId.For(b1), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
        _matchService.Received(1).FindUsages(BindingId.For(b2), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    [Fact]
    public void Method_with_one_expression_used_reports_only_unused_expression()
    {
        // b1 ("first expression") is matched in a feature file; b2 ("second expression") is not.
        var (b1, b2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 10,
            expression1: "first expression",
            expression2: "second expression");
        StubUsagesFor(b1, MakeMatchForBinding(b1));

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", b1, b2) });

        result.Should().ContainSingle("only the unused expression is reported; the used one is omitted");
        result.Single().BindingExpression.Should().Be("second expression");
    }

    [Fact]
    public void Method_with_all_expressions_used_is_not_reported()
    {
        var (b1, b2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 10,
            expression1: "first expression",
            expression2: "second expression");
        StubUsagesFor(b1, MakeMatchForBinding(b1));
        StubUsagesFor(b2, MakeMatchForBinding(b2));

        var result = CreateSut().FindUnusedStepDefinitions(new[] { MakeEntry("A", b1, b2) });

        result.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_multi_expression_methods_reports_only_unused_expressions()
    {
        // Method A (line 10): two expressions, neither used → 2 rows.
        // Method B (line 20): two expressions, first is used, second is not → 1 row (second).
        var (aB1, aB2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 10,
            method:      "StepDefs.UnusedMethod()",
            expression1: "unused expr A1",
            expression2: "unused expr A2");

        var (bB1, bB2) = MakeTwoExpressionsOnSameMethod("/ws/Steps.cs", line: 20,
            method:      "StepDefs.PartiallyUsedMethod()",
            expression1: "used expr B1",
            expression2: "unused expr B2");

        StubUsagesFor(bB1, MakeMatchForBinding(bB1));

        var result = CreateSut().FindUnusedStepDefinitions(
            new[] { MakeEntry("A", aB1, aB2, bB1, bB2) });

        // Expected: A1, A2 (method A both unused), B2 (method B's unused expression) = 3 rows.
        result.Should().HaveCount(3);
        result.Select(i => i.BindingExpression)
              .Should().BeEquivalentTo(
                  new[] { "unused expr A1", "unused expr A2", "unused expr B2" });
    }
}
