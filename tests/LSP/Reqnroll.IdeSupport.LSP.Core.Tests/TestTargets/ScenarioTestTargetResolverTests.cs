#nullable enable

using Reqnroll.IdeSupport.LSP.TestStubs;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.TestTargets;

public class ScenarioTestTargetResolverTests : IDisposable
{
    private readonly List<string> _tempFeaturePaths = new();
    private readonly List<string> _tempProjectFolders = new();
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

        foreach (var folder in _tempProjectFolders)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
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

    /// <summary>
    /// Mimics Reqnroll 3.3.0's <c>GenerateFeatureFileCodeBehindInProjectDirectory=false</c> option:
    /// the <c>.feature</c> file sits directly under a fresh temp "project folder", but its
    /// code-behind lands under that folder's <c>obj/</c> tree (at an arbitrary nested depth, since
    /// the real layout varies by configuration/TFM) instead of beside it.
    /// </summary>
    private Uri WriteGeneratedFixtureInObjFolder(string generatedCsContent, out string projectFolder)
    {
        projectFolder = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid()}");
        _tempProjectFolders.Add(projectFolder);
        Directory.CreateDirectory(projectFolder);

        var featurePath = Path.Combine(projectFolder, $"{Guid.NewGuid()}.feature");
        File.WriteAllText(featurePath, string.Empty);

        var objSubfolder = Path.Combine(projectFolder, "obj", "Debug", "net8.0");
        Directory.CreateDirectory(objSubfolder);
        File.WriteAllText(Path.Combine(objSubfolder, Path.GetFileName(featurePath) + ".cs"), generatedCsContent);

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

    /// <summary>Wraps a real <see cref="CSharpSyntaxTreeCache"/>, counting how many times a
    /// disk-mode parse was actually requested — used to confirm <see cref="ScenarioTestTargetResolver"/>
    /// goes through the shared cache rather than parsing the generated file itself (issue #491).</summary>
    private sealed class CountingSyntaxTreeCache : ICSharpSyntaxTreeCache
    {
        private readonly CSharpSyntaxTreeCache _inner = new();
        public List<SyntaxNode?> ReturnedRoots { get; } = new();

        public SyntaxNode? GetOrParseFromDisk(string filePath, IFileSystemForIDE fileSystem)
        {
            var root = _inner.GetOrParseFromDisk(filePath, fileSystem);
            ReturnedRoots.Add(root);
            return root;
        }

        public SyntaxNode GetOrParse(string filePath, string text) => _inner.GetOrParse(filePath, text);
        public void Invalidate(string filePath) => _inner.Invalidate(filePath);
    }

    // ── Plain scenario ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolving_the_same_file_twice_reuses_the_cached_parse_instead_of_re_reading_disk()
    {
        // Regression coverage for issue #491: RunTestCodeLensService calls Resolve once per
        // scenario/row in a file, which used to re-read and re-parse the same generated .feature.cs
        // from scratch on every single call. The resolver must go through the shared cache so a
        // second resolution against the same, unchanged file is a cache hit.
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
        var cache = new CountingSyntaxTreeCache();
        var sut = new ScenarioTestTargetResolver(cache);

        sut.Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);
        sut.Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        // The cache is consulted on every call (that's how it validates freshness), but the second
        // call must get back the exact same parsed root as the first — proof the resolver is
        // routing through the shared cache rather than re-parsing the file itself.
        cache.ReturnedRoots.Should().HaveCount(2);
        cache.ReturnedRoots[1].Should().BeSameAs(cache.ReturnedRoots[0]);
    }

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

    [Fact]
    public void Missing_generated_file_with_a_project_folder_but_no_obj_output_returns_empty_without_throwing()
    {
        var tags = ParseTags("Feature: F\nScenario: S\n    Given a step\n");
        var uri = new Uri(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.feature"));
        var projectFolder = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid()}");
        _tempProjectFolders.Add(projectFolder);
        Directory.CreateDirectory(projectFolder);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds, projectFolder);

        result.Should().BeEmpty();
    }

    // ── Reqnroll 3.3.0 obj-relocated code-behind ────────────────────────────────

    [Fact]
    public void Plain_scenario_resolves_when_the_code_behind_is_relocated_under_obj()
    {
        var text = "Feature: Calculator\nScenario: Add two numbers\n    Given a step\n";
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixtureInObjFolder("""
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void AddTwoNumbers()
                    {
                    }
                }
            }
            """, out var projectFolder);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds, projectFolder);

        result.Should().HaveCount(1);
        result[0].DeclaringTypeFullName.Should().Be("Tests.CalculatorFeature");
        result[0].MethodName.Should().Be("AddTwoNumbers");
    }

    [Fact]
    public void Obj_relocated_code_behind_is_ignored_without_a_project_folder()
    {
        // No projectFolder passed => only the co-located convention is tried; the resolver must not
        // guess at an obj/ location it was never told about.
        var text = "Feature: Calculator\nScenario: Add two numbers\n    Given a step\n";
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixtureInObjFolder("""
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void AddTwoNumbers()
                    {
                    }
                }
            }
            """, out _);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Co_located_code_behind_is_preferred_over_an_obj_relocated_one_when_both_exist()
    {
        // Belt-and-braces: if a stale obj/ copy lingers alongside a co-located file (e.g. after
        // toggling the MSBuild option), the co-located one — the one actually next to the .feature
        // file being edited — wins.
        var text = "Feature: Calculator\nScenario: Add two numbers\n    Given a step\n";
        var tags = ParseTags(text);
        var uri = WriteGeneratedFixtureInObjFolder("""
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void StaleMethod() { }
                }
            }
            """, out var projectFolder);
        File.WriteAllText(uri.LocalPath + ".cs", """
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void AddTwoNumbers() { }
                }
            }
            """);
        _tempFeaturePaths.Add(uri.LocalPath);

        var result = CreateSut().Resolve(uri, tags, RangeAtLine(tags, 1), XUnitPackageIds, projectFolder);

        result.Should().HaveCount(1);
        result[0].MethodName.Should().Be("AddTwoNumbers");
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
