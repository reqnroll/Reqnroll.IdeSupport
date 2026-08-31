namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

public class BindingImporterTests
{
    private readonly Dictionary<string, string> _sourceFiles = new();
    private readonly Dictionary<string, string> _typeNames = new();

    private BindingImporter CreateSut() => new(_sourceFiles, _typeNames, new IdeSupportNullLogger(), new FileSystemForIDE());

    private StepDefinition CreateStepDefinition(string? regex = null, string? type = null, string? sourceLocation = null,
        StepScope? scope = null, string? paramTypes = null, string? method = null, string? expression = null,
        string? error = null) =>
        new()
        {
            Method = method ?? "M1",
            Type = type ?? "Given",
            Regex = regex ?? "regex",
            SourceLocation = sourceLocation,
            Scope = scope,
            ParamTypes = paramTypes,
            Expression = expression,
            Error = error
        };

    [Fact]
    public void Parses_regex_with_full_match()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition("^my step$"));

        result.Regex.Should().NotBeNull();
        result.Regex.ToString().Should().Be("^my step$");
    }

    [Fact]
    public void Parses_regex_with_partial_match()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition("my step"));

        result.Regex.Should().NotBeNull();
        result.Regex.ToString().Should().Be("my step");
    }

    [Fact]
    public void Parses_type()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(type: "When"));

        result.StepDefinitionType.Should().Be(ScenarioBlock.When);
    }

    [Fact]
    public void Parses_source_location_start_only()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: "MyClass.cs|2|5"));

        result.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation.SourceFile.Should().Be("MyClass.cs");
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(2);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(5);
        result.Implementation.SourceLocation.HasEndPosition.Should().BeFalse();
    }

    [Fact]
    public void MethodIdentifierLocation_overrides_the_PDB_derived_source_location()
    {
        // Regression guard (issue #471 follow-up): ConnectorDiscoveryService backfills the AST-based
        // method-identifier location to replace the PDB sequence point, which can land inside the
        // method body rather than on the declaration itself. ImportStepDefinition must actually
        // apply that override, not just accept the parameter.
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(
            CreateStepDefinition(sourceLocation: "MyClass.cs|9|3"),
            attributeSourceLine: null,
            methodIdentifierLocation: (4, 17));

        result!.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation!.SourceFile.Should().Be("MyClass.cs");
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(4);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(17);
    }

    [Fact]
    public void MethodIdentifierLocation_override_is_ignored_when_null()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(
            CreateStepDefinition(sourceLocation: "MyClass.cs|9|3"),
            attributeSourceLine: null,
            methodIdentifierLocation: null);

        result!.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation!.SourceFileLine.Should().Be(9);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(3);
    }

    [Fact]
    public void Parses_source_location()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: "MyClass.cs|2|5|4|7"));

        result.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation.SourceFile.Should().Be("MyClass.cs");
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(2);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(5);
        result.Implementation.SourceLocation.HasEndPosition.Should().BeTrue();
        result.Implementation.SourceLocation.SourceFileEndLine.Should().Be(4);
        result.Implementation.SourceLocation.SourceFileEndColumn.Should().Be(7);
    }

    [Fact]
    public void Parses_source_location_from_file_reference()
    {
        _sourceFiles.Add("1", "MyClass.cs");
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: "#1|2|5"));

        result.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation.SourceFile.Should().Be("MyClass.cs");
    }

    [Fact]
    public void Parses_source_location_without_column()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: "MyClass.cs|2"));

        result.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(2);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(1);
    }

    [Fact]
    public void Parses_source_location_without_line_and_column()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: "MyClass.cs"));

        result.Implementation.SourceLocation.Should().NotBeNull();
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(1);
        result.Implementation.SourceLocation.SourceFileColumn.Should().Be(1);
    }

    [Fact]
    public void Parses_step_definition_without_scope()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(scope: null));

        result.Scope.Should().BeNull();
    }

    [Fact]
    public void Imports_to_valid_when_no_error()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition("^my step$"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Imports_expression_and_error()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition("^my step$",
            expression: "my step", error: "this is an error"));

        result.SpecifiedExpression.Should().Be("my step");
        result.Error.Should().Be("Invalid step definition: this is an error");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Imports_invalid_step_definition_with_null_regex()
    {
        var sut = CreateSut();
        var stepDefinition = CreateStepDefinition(error: "this is an error");
        stepDefinition.Regex = null;
        var result = sut.ImportStepDefinition(stepDefinition);

        result.Regex.Should().BeNull();
        result.Error.Should().Be("Invalid step definition: this is an error");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parses_step_definition_tag_scope()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(scope: new StepScope {Tag = "@mytag"}));

        result.Scope.Should().NotBeNull();
        result.Scope.Tag.Should().NotBeNull();
        result.Scope.Tag.ToString().Should().Be("@mytag");
    }

    [Fact]
    public void Parses_single_parameter_type()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: "MyNamespace.MyType"));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().HaveCount(1);
        result.Implementation.ParameterTypes[0].Should().Be("MyNamespace.MyType");
    }

    [Fact]
    public void Parses_type_name_with_assembly()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: "MyNamespace.MyType, MyAssembly"));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().HaveCount(1);
        result.Implementation.ParameterTypes[0].Should().Be("MyNamespace.MyType, MyAssembly");
    }

    [Fact]
    public void Parses_parameter_shortcuts()
    {
        var sut = CreateSut();
        var shortcuts = TypeShortcuts.FromShortcut.ToArray();
        var result =
            sut.ImportStepDefinition(CreateStepDefinition(paramTypes: string.Join("|", shortcuts.Select(s => s.Key))));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().Equal(shortcuts.Select(s => s.Value));
    }

    [Theory]
    [InlineData("s")]
    [InlineData("i")]
    [InlineData("s|s")]
    [InlineData("st")]
    public void Parse_usual_parameters_optimized(string paramTypes)
    {
        var sut = CreateSut();
        var result1 = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: paramTypes));
        var result2 = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: paramTypes));

        result2.Implementation.ParameterTypes.Should().BeSameAs(result1.Implementation.ParameterTypes);
    }

    [Fact]
    public void Parses_parameter_types()
    {
        var sut = CreateSut();
        var result =
            sut.ImportStepDefinition(CreateStepDefinition(paramTypes: "MyNamespace.MyType | MyNamespace.OtherType"));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().HaveCount(2);
        result.Implementation.ParameterTypes[0].Should().Be("MyNamespace.MyType");
        result.Implementation.ParameterTypes[1].Should().Be("MyNamespace.OtherType");
    }

    [Fact]
    public void Parses_parameter_types_from_external_list()
    {
        _typeNames.Add("1", "MyNamespace.MyType");
        _typeNames.Add("2", "MyNamespace.OtherType");
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: "#1 | #2"));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().HaveCount(2);
        result.Implementation.ParameterTypes[0].Should().Be("MyNamespace.MyType");
        result.Implementation.ParameterTypes[1].Should().Be("MyNamespace.OtherType");
    }

    [Fact]
    public void Parses_null_parameter_type_as_empty_array()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(paramTypes: null));

        result.Implementation.ParameterTypes.Should().NotBeNull();
        result.Implementation.ParameterTypes.Should().HaveCount(0);
    }

    [Fact]
    public void Merges_implementations()
    {
        var sut = CreateSut();
        var result1 = sut.ImportStepDefinition(CreateStepDefinition(method: "MyMethod", regex: "R1"));
        var result2 = sut.ImportStepDefinition(CreateStepDefinition(method: "MyMethod", regex: "R2"));

        result1.Implementation.Should().BeSameAs(result2.Implementation);
    }

    [Fact]
    public void Parses_step_definition_with_invalid_tag_scope()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(scope: new StepScope { Tag = "@foo ( wrong" }));

        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Scope.Should().NotBeNull();
        result.Scope.IsValid.Should().BeFalse();
        result.Scope.Error.Should().Contain("Invalid tag expression");
        result.Error.Should().Contain("Invalid tag expression");
    }

    [Fact]
    public void Parses_hook_with_invalid_tag_scope()
    {
        var sut = CreateSut();
        var result = sut.ImportHook(CreateHook(scope: new StepScope { Tag = "@foo ( wrong" }));

        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Scope.Should().NotBeNull();
        result.Scope.IsValid.Should().BeFalse();
        result.Scope.Error.Should().Contain("Invalid tag expression");
        result.Error.Should().Contain("Invalid tag expression");
    }

    [Fact]
    public void Parses_step_definition_with_invalid_tag_scope_and_source_error()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(
            scope: new StepScope { Tag = "@foo ( wrong" },
            error: "source error"));

        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Invalid step definition: source error");
        result.Scope.Should().NotBeNull();
        result.Scope.IsValid.Should().BeFalse();
        result.Scope.Error.Should().Contain("Invalid tag expression");
    }

    [Fact]
    public void Parses_hook_with_invalid_tag_scope_and_source_error()
    {
        var sut = CreateSut();
        var result = sut.ImportHook(CreateHook(scope: new StepScope { Tag = "@foo ( wrong" }, error: "source error"));

        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("Invalid hook: source error");
        result.Scope.Should().NotBeNull();
        result.Scope.IsValid.Should().BeFalse();
        result.Scope.Error.Should().Contain("Invalid tag expression");
    }

    private Hook CreateHook(string? type = null, string? sourceLocation = null,
        StepScope? scope = null, string? method = null, int? hookOrder = null, string? error = null) =>
        new()
        {
            Method = method ?? "M1",
            Type = type ?? "BeforeScenario",
            SourceLocation = sourceLocation,
            Scope = scope,
            HookOrder = hookOrder,
            Error = error
        };

    [Fact]
    public void ImportStepDefinition_sets_attribute_source_line_when_provided()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition(), attributeSourceLine: 42);

        result.AttributeSourceLine.Should().Be(42);
    }

    [Fact]
    public void ImportStepDefinition_leaves_attribute_source_line_null_when_not_provided()
    {
        var sut = CreateSut();
        var result = sut.ImportStepDefinition(CreateStepDefinition());

        result.AttributeSourceLine.Should().BeNull();
    }

    [Fact]
    public void ResolveSourceFilePath_returns_null_for_empty_location()
    {
        var sut = CreateSut();

        sut.ResolveSourceFilePath(null).Should().BeNull();
        sut.ResolveSourceFilePath("").Should().BeNull();
    }

    [Fact]
    public void ResolveSourceFilePath_resolves_index_reference_when_file_exists()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            _sourceFiles.Add("1", path);
            var sut = CreateSut();

            var result = sut.ResolveSourceFilePath("#1|2|5");

            result.Should().Be(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSourceFilePath_returns_null_for_unresolvable_index_reference()
    {
        var sut = CreateSut();

        var result = sut.ResolveSourceFilePath("#1|2|5");

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveSourceFilePath_returns_null_when_index_referenced_file_does_not_exist_on_this_machine()
    {
        // Reflection/PDB-based discovery records the absolute source path from the machine that
        // built the assembly. For an external or dynamically loaded plugin assembly (built on CI,
        // or on another dev machine) that path commonly does not exist locally; this must not be
        // treated as a resolvable path just because the #index table lookup itself succeeded.
        _sourceFiles.Add("1", @"C:\build-agent\plugin-repo\Steps.cs");
        var sut = CreateSut();

        var result = sut.ResolveSourceFilePath("#1|2|5");

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveSourceFilePath_returns_literal_path_when_file_exists()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var sut = CreateSut();
            var result = sut.ResolveSourceFilePath($"{path}|2|5");

            result.Should().Be(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSourceFilePath_returns_null_when_literal_path_does_not_exist()
    {
        var sut = CreateSut();

        var result = sut.ResolveSourceFilePath("MyClass.cs|2|5");

        result.Should().BeNull();
    }

    // ── Source-path resolution state (issue #540 F1/F2) ───────────────────────────────────

    [Fact]
    public void An_unresolvable_source_path_is_imported_as_an_unresolved_location()
    {
        // This is the incident: the assembly was built in a devcontainer, so every binding's source
        // path was /workspaces/... . The registry used to store it verbatim with nothing marking it
        // as unusable, and every navigation feature then handed it to the IDE.
        var sut = CreateSut();

        var result = sut.ImportStepDefinition(
            CreateStepDefinition(sourceLocation: "/workspaces/host-solution/Specs/Steps.cs|12|5"));

        var location = result.Implementation.SourceLocation;
        location.Should().NotBeNull();
        location!.IsResolved.Should().BeFalse();
        location.RecordedSourceFile.Should().Be("/workspaces/host-solution/Specs/Steps.cs");
        location.SourceFileLine.Should().Be(12);
    }

    [Fact]
    public void An_unresolvable_hook_source_path_is_imported_as_an_unresolved_location()
    {
        // Hooks were the visible casualty: they got no backfill at all, so nothing on their path
        // ever looked at whether the file existed.
        var sut = CreateSut();

        var result = sut.ImportHook(new Hook
        {
            Method = "Hooks.BeforeScenario()",
            Type = "BeforeScenario",
            SourceLocation = "/workspaces/host-solution/Specs/Support/Hooks.cs|9|3"
        });

        var location = result.Implementation.SourceLocation;
        location.Should().NotBeNull();
        location!.IsResolved.Should().BeFalse();
        location.RecordedSourceFile.Should().Be("/workspaces/host-solution/Specs/Support/Hooks.cs");
    }

    [Fact]
    public void A_resolvable_source_path_is_imported_as_a_resolved_location()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var sut = CreateSut();

            var result = sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: $"{path}|12|5"));

            result.Implementation.SourceLocation!.IsResolved.Should().BeTrue();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Unresolved_source_paths_are_reported_once_per_distinct_path()
    {
        // One devcontainer-built assembly yields one unresolved path per binding *file* but dozens
        // of bindings; the discovery run reports the distinct set, not one warning per binding.
        var sut = CreateSut();

        sut.ImportStepDefinition(CreateStepDefinition(method: "A", sourceLocation: "/workspaces/host/Steps.cs|1|1"));
        sut.ImportStepDefinition(CreateStepDefinition(method: "B", sourceLocation: "/workspaces/host/Steps.cs|9|1"));
        sut.ImportHook(new Hook
        {
            Method = "Hooks.BeforeScenario()",
            Type = "BeforeScenario",
            SourceLocation = "/workspaces/host/Hooks.cs|4|1"
        });

        sut.UnresolvedSourceFiles.Should().BeEquivalentTo(
            ["/workspaces/host/Steps.cs", "/workspaces/host/Hooks.cs"]);
    }

    [Fact]
    public void A_resolvable_import_reports_no_unresolved_source_files()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var sut = CreateSut();
            sut.ImportStepDefinition(CreateStepDefinition(sourceLocation: $"{path}|12|5"));

            sut.UnresolvedSourceFiles.Should().BeEmpty();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void A_backfilled_hook_identifier_location_replaces_the_pdb_position()
    {
        // Hooks now get the same method-identifier backfill step definitions have had since #471:
        // the PDB sequence point is the first executable statement, not the declaration line.
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var sut = CreateSut();

            var result = sut.ImportHook(
                new Hook { Method = "Hooks.BeforeScenario()", Type = "BeforeScenario", SourceLocation = $"{path}|11|9" },
                methodIdentifierLocation: (7, 17));

            result.Implementation.SourceLocation!.SourceFileLine.Should().Be(7);
            result.Implementation.SourceLocation.SourceFileColumn.Should().Be(17);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Backfilling_a_hook_position_does_not_mark_an_unresolved_location_resolved()
    {
        var sut = CreateSut();

        var result = sut.ImportHook(
            new Hook { Method = "Hooks.BeforeScenario()", Type = "BeforeScenario", SourceLocation = "/workspaces/host/Hooks.cs|11|9" },
            methodIdentifierLocation: (7, 17));

        result.Implementation.SourceLocation!.IsResolved.Should().BeFalse();
        result.Implementation.SourceLocation.SourceFileLine.Should().Be(7);
    }

    [Fact]
    public void A_supplied_resolver_remaps_a_foreign_path_at_import_time()
    {
        var projectFolder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "BindingImporterResolve_" + Guid.NewGuid());
        var localFile = System.IO.Path.Combine(projectFolder, "Support", "Hooks.cs");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localFile)!);
        System.IO.File.WriteAllText(localFile, "// test");
        try
        {
            var fileSystem = new FileSystemForIDE();
            var sut = new BindingImporter(_sourceFiles, _typeNames, new IdeSupportNullLogger(), fileSystem,
                new ProjectSourceFileResolver(projectFolder, fileSystem));

            var result = sut.ImportHook(new Hook
            {
                Method = "Hooks.BeforeScenario()",
                Type = "BeforeScenario",
                SourceLocation = "/workspaces/host-solution/Specs/Support/Hooks.cs|9|3"
            });

            var location = result.Implementation.SourceLocation;
            location!.IsResolved.Should().BeTrue();
            location.SourceFile.Should().Be(localFile);
            location.RecordedSourceFile.Should().Be("/workspaces/host-solution/Specs/Support/Hooks.cs");
            sut.UnresolvedSourceFiles.Should().BeEmpty();
        }
        finally
        {
            try { System.IO.Directory.Delete(projectFolder, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class BindingImporterTryGetAttributeSourceLineTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly string _tempDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "BindingImporterTests_" + Guid.NewGuid());

    public BindingImporterTryGetAttributeSourceLineTests() => System.IO.Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_tempDir))
            System.IO.Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSource(string content)
    {
        var path = System.IO.Path.Combine(_tempDir, Guid.NewGuid() + ".cs");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Returns_null_when_source_file_does_not_exist()
    {
        var missingPath = System.IO.Path.Combine(_tempDir, "does-not-exist.cs");

        var result = BindingImporter.TryGetAttributeSourceLine(missingPath, "MyStep", ScenarioBlock.Given, _fileSystem);

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_method_is_not_found()
    {
        var path = WriteSource("""
            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "NoSuchMethod", ScenarioBlock.Given, _fileSystem);

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_line_of_binding_attribute_above_the_method()
    {
        var path = WriteSource("""
            public class Steps
            {
                // line 3 is blank padding to make the asserted line non-trivial
                [Given("a step")]
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem);

        // The [Given] attribute is on line 4 (1-based) of the source above.
        result.Should().Be(4);
    }

    [Fact]
    public void Returns_null_when_method_exists_but_has_no_binding_attribute()
    {
        var path = WriteSource("""
            public class Steps
            {
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolves_the_correct_line_for_each_attribute_when_method_carries_multiple_binding_attributes()
    {
        // A single method with both [Given] and [When] is imported as two separate wire
        // StepDefinition DTOs (one per Type) that share the same Method name; each must resolve
        // to *its own* attribute's line rather than collapsing onto whichever is scanned first.
        var path = WriteSource("""
            public class Steps
            {
                [Given("given text")]
                [When("when text")]
                public void MyStep() { }
            }
            """);

        BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem).Should().Be(3);
        BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.When, _fileSystem).Should().Be(4);
    }

    [Fact]
    public void A_StepDefinition_attribute_matches_any_scenario_block()
    {
        // [StepDefinition] registers for Given, When and Then alike.
        var path = WriteSource("""
            public class Steps
            {
                [StepDefinition("a step")]
                public void MyStep() { }
            }
            """);

        BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem).Should().Be(3);
        BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.When, _fileSystem).Should().Be(3);
        BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Then, _fileSystem).Should().Be(3);
    }

    [Fact]
    public void Continues_past_an_earlier_same_named_method_that_lacks_a_binding_attribute()
    {
        // If a file has two methods with the same name (e.g. a helper and the real step method),
        // encountering the non-bound one first must not stop the scan from finding the real one.
        var path = WriteSource("""
            public class Helpers
            {
                public void MyStep() { }
            }

            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem);

        result.Should().Be(8);
    }

    [Fact]
    public void Matches_a_namespace_qualified_attribute()
    {
        // GetAttributeName (shared with StepDefinitionFileParser) strips namespace qualification
        // and the "Attribute" suffix generically, so a fully-qualified attribute is matched too.
        var path = WriteSource("""
            public class Steps
            {
                [Reqnroll.Given("a step")]
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Given, _fileSystem);

        result.Should().Be(3);
    }

    [Fact]
    public void Unknown_scenario_block_matches_any_binding_attribute()
    {
        var path = WriteSource("""
            public class Steps
            {
                [When("a step")]
                public void MyStep() { }
            }
            """);

        var result = BindingImporter.TryGetAttributeSourceLine(path, "MyStep", ScenarioBlock.Unknown, _fileSystem);

        result.Should().Be(3);
    }

    [Fact]
    public void TryParseSourceFile_then_TryGetAttributeSourceLine_overload_produces_the_same_result()
    {
        var path = WriteSource("""
            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }
            }
            """);

        var root = BindingImporter.TryParseSourceFile(path, _fileSystem);
        var result = BindingImporter.TryGetAttributeSourceLine(root, "MyStep", ScenarioBlock.Given);

        result.Should().Be(3);
    }

    [Fact]
    public void TryParseSourceFile_returns_null_when_file_does_not_exist()
    {
        var missingPath = System.IO.Path.Combine(_tempDir, "does-not-exist.cs");

        var result = BindingImporter.TryParseSourceFile(missingPath, _fileSystem);

        result.Should().BeNull();
    }
}

public class BindingImporterTryGetMethodIdentifierLocationTests : IDisposable
{
    private readonly IFileSystemForIDE _fileSystem = new FileSystemForIDE();
    private readonly string _tempDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "BindingImporterTests_" + Guid.NewGuid());

    public BindingImporterTryGetMethodIdentifierLocationTests() => System.IO.Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_tempDir))
            System.IO.Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteSource(string content)
    {
        var path = System.IO.Path.Combine(_tempDir, Guid.NewGuid() + ".cs");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Returns_null_when_method_is_not_found()
    {
        var path = WriteSource("""
            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }
            }
            """);
        var root = BindingImporter.TryParseSourceFile(path, _fileSystem);

        var result = BindingImporter.TryGetMethodIdentifierLocation(root, "NoSuchMethod");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_the_method_identifiers_own_line_and_column_not_the_attributes()
    {
        // Regression guard (issue #471 follow-up): the connector's own wire-format location is a
        // PDB sequence point, which can land a line or more into the method body. This backfill
        // must resolve to the method identifier's own position — the same convention
        // StepDefinitionFileParser.GetSourceLocation already uses for Roslyn-discovered bindings —
        // so CodeLens (and anything else keyed on SourceLocation.SourceFileLine) anchors
        // consistently regardless of which discovery path populated the registry.
        var path = WriteSource("""
            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }
            }
            """);
        var root = BindingImporter.TryParseSourceFile(path, _fileSystem);

        var result = BindingImporter.TryGetMethodIdentifierLocation(root, "MyStep");

        // "    public void MyStep() { }" is line 4 (1-based); "MyStep" starts at column 17.
        result.Should().Be((4, 17));
    }

    [Fact]
    public void Resolves_the_first_matching_method_when_multiple_share_a_name_across_overloads()
    {
        var path = WriteSource("""
            public class Steps
            {
                [Given("a step")]
                public void MyStep() { }

                [Given("another step")]
                public void MyStep(int n) { }
            }
            """);
        var root = BindingImporter.TryParseSourceFile(path, _fileSystem);

        var result = BindingImporter.TryGetMethodIdentifierLocation(root, "MyStep");

        result!.Value.Line.Should().Be(4);
    }
}
