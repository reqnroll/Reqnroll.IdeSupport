#nullable enable

using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestTargets;

public class ScenarioTestTargetResolverTests : IDisposable
{
    private readonly List<string> _tempFeaturePaths = new();
    private static readonly string[] XUnitPackageIds = { "Reqnroll.xUnit" };

    public void Dispose()
    {
        foreach (var path in _tempFeaturePaths)
        {
            try
            {
                if (File.Exists(path + ".cs"))
                    File.Delete(path + ".cs");
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────

    private static IReadOnlyCollection<DeveroomTag> ParseTags(string text)
    {
        var logger = Substitute.For<IIdeSupportLogger>();
        var telemetry = Substitute.For<ITelemetryService>();
        var configProvider = Substitute.For<IDeveroomConfigurationProvider>();
        configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
        var parser = new DeveroomTagParser(logger, telemetry, configProvider);
        return parser.Parse(new StubGherkinTextSnapshot(text), ProjectBindingRegistry.Invalid);
    }

    private Uri WriteGeneratedFixture(string generatedCsContent)
    {
        var featurePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.feature");
        _tempFeaturePaths.Add(featurePath);
        File.WriteAllText(featurePath + ".cs", generatedCsContent);
        return new Uri(featurePath);
    }

    private static GherkinRange RangeAtLine(IReadOnlyCollection<DeveroomTag> tags, int lineNumber)
    {
        var snapshot = tags.First().Range.Snapshot;
        var line = snapshot.GetLineFromLineNumber(lineNumber);
        var length = Math.Max(1, line.End - line.Start);
        return GherkinRange.FromPoint(snapshot, line.Start, length);
    }

    /// <summary>Finds the range at the first line of <paramref name="text"/> containing <paramref name="substring"/> — avoids hand-counting line numbers in the fixtures above.</summary>
    private static GherkinRange RangeAtLineContaining(IReadOnlyCollection<DeveroomTag> tags, string text, string substring)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var lineNumber = Array.FindIndex(lines, l => l.Contains(substring, StringComparison.Ordinal));
        if (lineNumber < 0)
            throw new InvalidOperationException($"No line containing '{substring}' in the fixture text.");
        return RangeAtLine(tags, lineNumber);
    }

    private static ScenarioTestTargetResolver CreateSut() => new();

    // ── Plain scenario ───────────────────────────────────────────────────────────

    [Fact]
    public void Plain_scenario_resolves_to_a_single_non_parameterized_target()
    {
        var text = "Feature: Calculator\nScenario: Add two numbers\n    Given a step\n";
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixture("""
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void AddTwoNumbers()
                    {
                    }
                }
            }
            """);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().HaveCount(1);
        result[0].DeclaringTypeFullName.Should().Be("Tests.CalculatorFeature");
        result[0].MethodName.Should().Be("AddTwoNumbers");
        result[0].IsParameterized.Should().BeFalse();
        result[0].RowIndex.Should().BeNull();
    }

    // ── Row-tests Scenario Outline ──────────────────────────────────────────────

    private const string RowTestsFeatureText = """
        Feature: F
        Scenario Outline: Add numbers
            Given the first number is <a>
            And the second number is <b>
            Then the result is <c>

            Examples:
                | a  | b  | c  |
                | 1  | 2  | 3  |
                | 4  | 5  | 9  |

            Examples: Negative cases
                | a  | b  | c  |
                | -1 | -2 | -3 |

        """;

    private const string RowTestsGeneratedCs = """
        namespace Tests
        {
            public class FFeature
            {
                [InlineData("1", "2", "3")]
                [InlineData("4", "5", "9")]
                [InlineData("-1", "-2", "-3")]
                public void AddNumbers()
                {
                }
            }
        }
        """;

    [Fact]
    public void RowTests_outline_header_resolves_to_one_target_per_row_sharing_the_method_name()
    {
        var tags = ParseTags(RowTestsFeatureText);
        var uri = WriteGeneratedFixture(RowTestsGeneratedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().HaveCount(3);
        result.Should().OnlyContain(t => t.MethodName == "AddNumbers" && t.IsParameterized);
        result.Select(t => t.RowIndex).Should().BeEquivalentTo(new int?[] { 0, 1, 2 });
    }

    [Fact]
    public void RowTests_outline_header_correlates_row_arguments_positionally()
    {
        var tags = ParseTags(RowTestsFeatureText);
        var uri = WriteGeneratedFixture(RowTestsGeneratedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result[1].RowArguments.Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "4", ["b"] = "5", ["c"] = "9" });
    }

    [Fact]
    public void RowTests_specific_examples_row_resolves_to_just_that_row()
    {
        var tags = ParseTags(RowTestsFeatureText);
        var uri = WriteGeneratedFixture(RowTestsGeneratedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLineContaining(tags, RowTestsFeatureText, "| 4"), XUnitPackageIds);

        result.Should().HaveCount(1);
        result[0].RowIndex.Should().Be(1);
        result[0].RowArguments.Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "4", ["b"] = "5", ["c"] = "9" });
    }

    // ── Individual-methods Scenario Outline (allowRowTests = false) ────────────

    private const string IndividualMethodsFeatureText = """
        Feature: F
        Scenario Outline: Check value
            Given the value is <v>

            Examples:
                | v |
                | 1 |
                | 2 |

            Examples: Extra
                | v |
                | 3 |

        """;

    private const string IndividualMethodsGeneratedCs = """
        namespace Tests
        {
            public class FFeature
            {
                public void CheckValue__1() { }
                public void CheckValue__2() { }
                public void CheckValue_Extra__3() { }
            }
        }
        """;

    [Fact]
    public void IndividualMethods_outline_header_resolves_to_one_target_per_generated_method()
    {
        var tags = ParseTags(IndividualMethodsFeatureText);
        var uri = WriteGeneratedFixture(IndividualMethodsGeneratedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().HaveCount(3);
        result.Should().OnlyContain(t => !t.IsParameterized && t.RowIndex == null);
        result.Select(t => t.MethodName).Should()
            .BeEquivalentTo(new[] { "CheckValue__1", "CheckValue__2", "CheckValue_Extra__3" });
    }

    [Fact]
    public void IndividualMethods_specific_examples_row_resolves_to_the_matching_generated_method()
    {
        var tags = ParseTags(IndividualMethodsFeatureText);
        var uri = WriteGeneratedFixture(IndividualMethodsGeneratedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLineContaining(tags, IndividualMethodsFeatureText, "| 3 |"), XUnitPackageIds);

        result.Should().HaveCount(1);
        result[0].MethodName.Should().Be("CheckValue_Extra__3");
    }

    [Fact]
    public void IndividualMethods_duplicate_first_cell_values_use_Variant_N_naming()
    {
        var text = """
            Feature: F
            Scenario Outline: Check value
                Given the value is <v>

                Examples:
                    | v | w |
                    | x | 1 |
                    | x | 2 |

            """;
        var generatedCs = """
            namespace Tests
            {
                public class FFeature
                {
                    public void CheckValue_Variant0() { }
                    public void CheckValue_Variant1() { }
                }
            }
            """;
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixture(generatedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLineContaining(tags, text, "| x | 2 |"), XUnitPackageIds);

        result.Should().HaveCount(1);
        result[0].MethodName.Should().Be("CheckValue_Variant1");
    }

    // ── AST-transforming generator plugin regression (design doc §2/§7 item 8) ─

    [Fact]
    public void Plain_scenario_with_no_visible_examples_still_reports_row_tests_when_generated_method_is_parameterized()
    {
        // Mimics Reqnroll.ExternalData: a plain Scenario:, no Outline keyword, no Examples: block
        // at all in the .feature file, whose generated method is nonetheless row-tests-parameterized.
        var text = "Feature: F\nScenario: Run per row\n    Given a step\n";
        var generatedCs = """
            namespace Tests
            {
                public class FFeature
                {
                    [InlineData("x")]
                    [InlineData("y")]
                    public void RunPerRow()
                    {
                    }
                }
            }
            """;
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixture(generatedCs);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.MethodName == "RunPerRow" && t.IsParameterized && t.RowArguments == null);
    }

    // ── Not-yet-built ────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_generated_file_returns_empty_without_throwing()
    {
        var tags = ParseTags("Feature: F\nScenario: S\n    Given a step\n");
        var uri = new Uri(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.feature"));

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().BeEmpty();
    }

    // ── ReqnrollIdentifierNaming ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Add two numbers", "AddTwoNumbers")]
    [InlineData("café", "Cafe")]
    [InlineData("123abc", "_123Abc")]
    [InlineData("it's a test", "ItsATest")]
    public void ToIdentifier_matches_the_decompiled_Reqnroll_algorithm(string input, string expected)
    {
        ReqnrollIdentifierNaming.ToIdentifier(input).Should().Be(expected);
    }
}
