using System.Collections.Concurrent;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Connector.AssemblyReflection;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery.Connector;

/// <summary>
/// Issue #928 -- scenarios A1, A6, C1 and C4: what the registry built from a compiled assembly may
/// contain when the working tree has moved on since that assembly was built (a branch switch with
/// no rebuild).
/// </summary>
/// <remarks>
/// The live repro: the DLL was built on a branch that had <c>Support/PriceCalculationHooks.cs</c>;
/// the checked-out branch does not. The connector still reports the DLL's three hooks, the registry
/// keeps them, and the feature file shows hook CodeLenses for hooks that no longer exist in source.
/// <para>
/// The tests pin down both halves of any fix. <b>Stale</b> bindings -- source path inside the
/// project folder, file gone -- must not survive. <b>Foreign</b> bindings -- source path outside
/// anything this machine could hold (a NuGet package, a CI agent, a container) -- must be kept,
/// because they will never have local source and are not stale.
/// </para>
/// <para>
/// Issue #933 widens "inside the project folder" to "inside the project folder or the workspace
/// folder that contains it", so a deleted shared-project file beside the project folder counts too.
/// </para>
/// </remarks>
public class ConnectorDiscoveryStaleBindingTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IOutProcConnectorFactory _factory = Substitute.For<IOutProcConnectorFactory>();
    // The workspace (solution) folder, with the project folder one level below it -- the layout of
    // the Quickstart solution, where SharedProject1\ is a sibling of the test project's folder.
    private readonly string _workspaceFolder = Path.Combine(Path.GetTempPath(), "StaleBindings_" + Guid.NewGuid().ToString("N"));
    private readonly string _projectFolder;
    private readonly string _assemblyPath;

    public ConnectorDiscoveryStaleBindingTests()
    {
        _projectFolder = Path.Combine(_workspaceFolder, "MyApp.Tests");
        Directory.CreateDirectory(_projectFolder);
        _assemblyPath = Path.Combine(_projectFolder, "MyApp.Tests.dll");
        File.WriteAllText(_assemblyPath, "not a real assembly");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceFolder))
            Directory.Delete(_workspaceFolder, recursive: true);
    }

    // ── Fakes / helpers ───────────────────────────────────────────────────────

    private sealed class FakeConnector : OutProcReqnrollConnector
    {
        private readonly DiscoveryResult _result;

        public FakeConnector(DiscoveryResult result)
            : base(
                new IdeSupportConfiguration(),
                Substitute.For<IIdeSupportLogger>(),
                TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
                AppContext.BaseDirectory,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(
                    TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
                NullLspTelemetryService.Instance)
        {
            _result = result;
        }

        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath) => _result;
        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }

    private IProjectScope MakeScope()
    {
        var scope = Substitute.For<IProjectScope>();
        scope.OutputAssemblyPath.Returns(_assemblyPath);
        scope.ProjectName.Returns("MyApp.Tests");
        scope.ProjectFolder.Returns(_projectFolder);
        scope.TargetFrameworkMoniker.Returns(".NETCoreApp,Version=v8.0");
        scope.Properties.Returns(new ConcurrentDictionary<Type, object>());
        scope.IdeScope.FileSystem.Returns(new FileSystemForIDE());
        scope.PackageReferences.Returns([
            new NuGetPackageReference("Reqnroll.MsTest", new NuGetVersion("2.1.0", "2.1.0"), null)
        ]);
        return scope;
    }

    /// <summary>Path of a file inside the project folder; the file is only created if <paramref name="content"/> is given.</summary>
    private string ProjectFile(string relativePath, string? content = null)
    {
        var path = Path.Combine(_projectFolder, relativePath);
        if (content is null)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string HooksSource = """
        public class MyHooks
        {
            [BeforeScenario]
            public void BeforeScenarioHook() { }
        }
        """;

    private static Hook BeforeScenarioHook(string sourceLocation) => new()
    {
        Type = "BeforeScenario",
        HookOrder = 10000,
        Method = "MyHooks.BeforeScenarioHook()",
        SourceLocation = sourceLocation
    };

    private static StepDefinition GivenStep(string name, string sourceLocation) => new()
    {
        Type = "Given",
        Regex = $"^{name}$",
        Method = $"MyApp.Steps.{name}()",
        Expression = name,
        SourceLocation = sourceLocation
    };

    /// <summary>
    /// The project as the server really holds it: an <see cref="LspReqnrollProject"/> built from a
    /// <c>reqnroll/projectLoaded</c> payload, which carries the client's workspace folder (VS: the
    /// solution folder; VS Code: the opened folder containing the project; Rider: the solution
    /// folder).
    /// </summary>
    private LspReqnrollProject MakeLspProject(string workspaceFolder)
    {
        var ideScope = Substitute.For<IIdeScope>();
        ideScope.FileSystem.Returns(new FileSystemForIDE());
        return new LspReqnrollProject(new ReqnrollProjectLoadedParams
        {
            WorkspaceFolder = workspaceFolder,
            ProjectFile = Path.Combine(_projectFolder, "MyApp.Tests.csproj"),
            ProjectFolder = _projectFolder,
            OutputAssemblyPath = _assemblyPath,
            TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0",
            PackageReferences = [new PackageReferenceInfo { PackageId = "Reqnroll.MsTest", Version = "2.1.0" }]
        }, ideScope);
    }

    private ProjectBindingRegistry Discover(DiscoveryResult result) => Discover(result, MakeScope());

    private ProjectBindingRegistry Discover(DiscoveryResult result, IProjectScope scope)
    {
        _factory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(result));
        var sut = new ConnectorDiscoveryService(_logger, _factory, new FileSystemForIDE());

        var (registry, _) = sut.RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);
        return registry;
    }

    /// <summary>Path of a file in the workspace folder, beside the project folder; created only if <paramref name="content"/> is given.</summary>
    private string WorkspaceFile(string relativePath, string? content = null)
    {
        var path = Path.Combine(_workspaceFolder, relativePath);
        if (content is null)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── A1: DLL built on branch A; branch B deleted the source files, no rebuild ──

    [Fact]
    public void A1_hook_whose_source_file_under_the_project_folder_was_deleted_is_not_kept()
    {
        var deletedHooksFile = ProjectFile(Path.Combine("Support", "PriceCalculationHooks.cs"));
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [],
            Hooks = [BeforeScenarioHook("#1|11|44")],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedHooksFile }
        });

        registry.Hooks.Should().BeEmpty(
            "the file that declared the hook is gone from the working tree, so the compiled hook is stale");
    }

    [Fact]
    public void A1_step_definition_whose_source_file_under_the_project_folder_was_deleted_is_not_kept()
    {
        var deletedStepsFile = ProjectFile(Path.Combine("StepDefinitions", "RemovedSteps.cs"));
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_removed_step", "#1|12|5")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedStepsFile }
        });

        registry.StepDefinitions.Should().BeEmpty();
    }

    [Fact]
    public void A1_only_the_bindings_from_deleted_files_are_dropped()
    {
        var survivingFile = ProjectFile(Path.Combine("Support", "Hooks.cs"), HooksSource);
        var deletedFile = ProjectFile(Path.Combine("Support", "OldSteps.cs"));
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_removed_step", "#2|12|5")],
            Hooks = [BeforeScenarioHook("#1|4|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = survivingFile, ["2"] = deletedFile }
        });

        registry.Hooks.Should().ContainSingle("its source file is still in the working tree");
        registry.StepDefinitions.Should().BeEmpty("its source file is gone");
    }

    [Fact]
    public void A1_dropping_stale_bindings_is_logged_once_with_the_count_and_the_remedy()
    {
        var deletedFile = ProjectFile(Path.Combine("Support", "PriceCalculationHooks.cs"));
        Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_removed_step", "#1|12|5")],
            Hooks = [BeforeScenarioHook("#1|11|44"), BeforeScenarioHook("#1|14|39")],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedFile }
        });

        _logger.Received(1).Log(Arg.Is<LogMessage>(m =>
            m.Level == System.Diagnostics.TraceLevel.Warning &&
            m.Message.Contains("Ignored 3 binding(s)") &&
            m.Message.Contains("1 file(s)") &&
            m.Message.Contains("rebuild")));
    }

    [Fact]
    public void A1_stale_paths_are_not_misreported_as_built_on_another_machine()
    {
        var deletedFile = ProjectFile(Path.Combine("Support", "PriceCalculationHooks.cs"));
        Discover(new DiscoveryResult
        {
            StepDefinitions = [],
            Hooks = [BeforeScenarioHook("#1|11|44")],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedFile }
        });

        _logger.DidNotReceive().Log(Arg.Is<LogMessage>(m =>
            m.Level == System.Diagnostics.TraceLevel.Warning &&
            m.Message.Contains("do not exist on this machine")));
    }

    // ── A6: source present (DLL current with the tree) -- nothing may be dropped ──

    [Fact]
    public void A6_bindings_whose_source_files_exist_are_all_kept()
    {
        var hooksFile = ProjectFile(Path.Combine("Support", "Hooks.cs"), HooksSource);
        var stepsFile = ProjectFile(Path.Combine("StepDefinitions", "Steps.cs"), """
            public class Steps
            {
                [Given("a_current_step")]
                public void a_current_step() { }
            }
            """);
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_current_step", "#2|3|5")],
            Hooks = [BeforeScenarioHook("#1|4|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = hooksFile, ["2"] = stepsFile }
        });

        registry.Hooks.Should().ContainSingle();
        registry.StepDefinitions.Should().ContainSingle();
    }

    // ── C1: external assembly (NuGet package) -- no local source, ever ────────

    [Fact]
    public void C1_binding_from_a_nuget_package_source_path_outside_the_workspace_is_kept()
    {
        var packageSource = Path.Combine(
            Path.GetTempPath(), "no-such-nuget-cache", "acme.reqnroll.steps", "1.2.3", "src", "AcmeSteps.cs");
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("an_acme_step", "#1|20|5")],
            Hooks = [BeforeScenarioHook("#1|30|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = packageSource }
        });

        registry.StepDefinitions.Should().ContainSingle("matching must keep working for external bindings");
        registry.Hooks.Should().ContainSingle();
    }

    // ── C4: assembly built elsewhere (container / CI) with foreign source paths ──

    [Theory]
    [InlineData(@"C:\build-agent\work\1\s\Specs\Support\Hooks.cs")]
    [InlineData("/src/app/Specs/Support/Hooks.cs")]
    [InlineData("/home/runner/work/repo/repo/Specs/Support/Hooks.cs")]
    public void C4_binding_with_a_foreign_build_machine_path_is_kept(string foreignPath)
    {
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_ci_built_step", "#1|20|5")],
            Hooks = [BeforeScenarioHook("#1|30|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = foreignPath }
        });

        registry.StepDefinitions.Should().ContainSingle();
        registry.Hooks.Should().ContainSingle();
    }

    [Fact]
    public void C4_foreign_path_that_remaps_onto_the_project_folder_is_kept_and_resolved()
    {
        // Built in a container at /src/Specs/..., checked out here under the project folder: the
        // resolver's prefix-remap finds the file, so the binding is neither stale nor unresolved.
        ProjectFile(Path.Combine("Support", "Hooks.cs"), HooksSource);
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [],
            Hooks = [BeforeScenarioHook("#1|4|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = "/src/Specs/Support/Hooks.cs" }
        });

        registry.Hooks.Should().ContainSingle();
    }

    [Fact]
    public void C4_binding_with_no_recorded_source_location_is_kept()
    {
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_step_without_pdb", sourceLocation: null!)],
            Hooks = [BeforeScenarioHook(null!)]
        });

        registry.StepDefinitions.Should().ContainSingle();
        registry.Hooks.Should().ContainSingle();
    }

    // ── C2 / C5: source that exists, but outside the project folder ───────────

    [Fact]
    public void C2_binding_from_a_project_reference_whose_source_exists_locally_is_kept()
    {
        var siblingFolder = Path.Combine(Path.GetTempPath(), "StaleBindingsRef_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(siblingFolder);
            var referencedSource = Path.Combine(siblingFolder, "RefSteps.cs");
            File.WriteAllText(referencedSource, """
                public class Steps
                {
                    [Given("a_referenced_step")]
                    public void a_referenced_step() { }
                }
                """);

            var registry = Discover(new DiscoveryResult
            {
                StepDefinitions = [GivenStep("a_referenced_step", "#1|3|5")],
                Hooks = [],
                SourceFiles = new Dictionary<string, string> { ["1"] = referencedSource }
            });

            registry.StepDefinitions.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(siblingFolder))
                Directory.Delete(siblingFolder, recursive: true);
        }
    }

    [Fact]
    public void C5_binding_from_a_shared_project_file_beside_the_project_folder_is_kept_while_the_file_exists()
    {
        // Mirrors Quickstart's SharedProject1\Class1.cs: a sibling of the project folder, not under it.
        var sharedFolder = Path.Combine(Path.GetTempPath(), "StaleBindingsShared_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sharedFolder);
            var shared = Path.Combine(sharedFolder, "Class1.cs");
            File.WriteAllText(shared, "public class Class1 { [Given(\"a_shared_step\")] public void a_shared_step() { } }");

            var registry = Discover(new DiscoveryResult
            {
                StepDefinitions = [GivenStep("a_shared_step", "#1|1|30")],
                Hooks = [],
                SourceFiles = new Dictionary<string, string> { ["1"] = shared }
            });

            registry.StepDefinitions.Should().ContainSingle();
        }
        finally
        {
            if (Directory.Exists(sharedFolder))
                Directory.Delete(sharedFolder, recursive: true);
        }
    }

    // ── C5 (issue #933): shared-project file beside the project folder, inside the workspace ──

    [Fact]
    public void C5_binding_from_a_deleted_shared_project_file_under_the_workspace_folder_is_not_kept()
    {
        // The live repro: Quickstart\SharedProject1\Class1.cs, gone on the checked-out branch, while
        // the DLL built on the other branch still reports Class1.GivenAnUsedStep().
        var deletedShared = WorkspaceFile(Path.Combine("SharedProject1", "Class1.cs"));
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_shared_step", "#1|1|30")],
            Hooks = [BeforeScenarioHook("#1|5|5")],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedShared }
        }, MakeLspProject(_workspaceFolder));

        registry.StepDefinitions.Should().BeEmpty(
            "the shared-project file is inside the workspace folder and gone, so the compiled step is stale");
        registry.Hooks.Should().BeEmpty();
    }

    [Fact]
    public void C5_dropping_a_deleted_shared_project_binding_is_counted_in_the_ignored_log_line()
    {
        var deletedShared = WorkspaceFile(Path.Combine("SharedProject1", "Class1.cs"));
        Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_shared_step", "#1|1|30")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedShared }
        }, MakeLspProject(_workspaceFolder));

        _logger.Received(1).Log(Arg.Is<LogMessage>(m =>
            m.Level == System.Diagnostics.TraceLevel.Warning &&
            m.Message.Contains("Ignored 1 binding(s)") &&
            m.Message.Contains(_workspaceFolder)));
        _logger.DidNotReceive().Log(Arg.Is<LogMessage>(m =>
            m.Level == System.Diagnostics.TraceLevel.Warning &&
            m.Message.Contains("do not exist on this machine")));
    }

    [Fact]
    public void C5_binding_from_an_existing_shared_project_file_under_the_workspace_folder_is_kept()
    {
        var shared = WorkspaceFile(Path.Combine("SharedProject1", "Class1.cs"),
            "public class Class1 { [Given(\"a_shared_step\")] public void a_shared_step() { } }");
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_shared_step", "#1|1|30")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = shared }
        }, MakeLspProject(_workspaceFolder));

        registry.StepDefinitions.Should().ContainSingle();
    }

    [Fact]
    public void C5_deleted_project_folder_binding_is_still_dropped_when_a_workspace_folder_is_known()
    {
        var deletedFile = ProjectFile(Path.Combine("Support", "PriceCalculationHooks.cs"));
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [],
            Hooks = [BeforeScenarioHook("#1|11|44")],
            SourceFiles = new Dictionary<string, string> { ["1"] = deletedFile }
        }, MakeLspProject(_workspaceFolder));

        registry.Hooks.Should().BeEmpty();
    }

    // ── C1/C4 with a workspace folder: foreign paths are still kept ───────────

    [Theory]
    [InlineData(@"C:\build-agent\work\1\s\Specs\Support\Hooks.cs")]
    [InlineData("/home/runner/work/repo/repo/Specs/Support/Hooks.cs")]
    public void C4_foreign_build_machine_path_is_kept_when_a_workspace_folder_is_known(string foreignPath)
    {
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_ci_built_step", "#1|20|5")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = foreignPath }
        }, MakeLspProject(_workspaceFolder));

        registry.StepDefinitions.Should().ContainSingle();
    }

    [Fact]
    public void C1_missing_source_outside_the_workspace_folder_is_kept()
    {
        // A sibling of the workspace folder: another repository's sources, or a package's.
        var outside = Path.Combine(
            Path.GetDirectoryName(_workspaceFolder)!, "OtherRepo_" + Guid.NewGuid().ToString("N"), "Steps.cs");
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("another_repos_step", "#1|20|5")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = outside }
        }, MakeLspProject(_workspaceFolder));

        registry.StepDefinitions.Should().ContainSingle();
    }

    [Fact]
    public void C1_workspace_folder_that_does_not_contain_the_project_is_not_used()
    {
        // VS Code falls back to its first workspace folder when none contains the project. That
        // folder says nothing about where this project's sources live, so it must not widen the rule.
        var unrelatedWorkspace = Path.Combine(Path.GetTempPath(), "StaleBindingsOther_" + Guid.NewGuid().ToString("N"));
        var missingThere = Path.Combine(unrelatedWorkspace, "SharedProject1", "Class1.cs");
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_shared_step", "#1|1|30")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = missingThere }
        }, MakeLspProject(unrelatedWorkspace));

        registry.StepDefinitions.Should().ContainSingle();
    }

    [Fact]
    public void C1_workspace_folder_that_is_a_file_system_root_is_not_used()
    {
        // A drive or file-system root would make every missing path on it "owned", including a
        // build agent's paths. Only the project folder rule applies then.
        var root = Path.GetPathRoot(_workspaceFolder)!;
        var missingOnSameDrive = Path.Combine(root, "build-agent-" + Guid.NewGuid().ToString("N"), "Steps.cs");
        var registry = Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_ci_built_step", "#1|20|5")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = missingOnSameDrive }
        }, MakeLspProject(root));

        registry.StepDefinitions.Should().ContainSingle();
    }

    // ── C3: a missing source file is reported, not silently ignored ───────────

    [Fact]
    public void C3_missing_foreign_source_is_still_reported_in_the_log()
    {
        Discover(new DiscoveryResult
        {
            StepDefinitions = [GivenStep("a_plugin_step", "#1|10|5")],
            Hooks = [],
            SourceFiles = new Dictionary<string, string> { ["1"] = @"C:\build-agent\plugin-repo\Steps.cs" }
        });

        _logger.Received().Log(Arg.Is<LogMessage>(m =>
            m.Level == System.Diagnostics.TraceLevel.Warning &&
            m.Message.Contains("do not exist on this machine")));
    }
}
