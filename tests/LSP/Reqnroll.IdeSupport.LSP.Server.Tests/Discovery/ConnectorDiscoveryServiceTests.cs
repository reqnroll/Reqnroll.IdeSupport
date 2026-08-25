using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Discovery;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Discovery;

public class ConnectorDiscoveryServiceTests : IDisposable
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly IOutProcConnectorFactory _factory = Substitute.For<IOutProcConnectorFactory>();
    private readonly string _projectFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string _assemblyPath;

    public ConnectorDiscoveryServiceTests()
    {
        Directory.CreateDirectory(_projectFolder);
        _assemblyPath = Path.Combine(_projectFolder, "MyApp.Tests.dll");
        File.WriteAllText(_assemblyPath, "not a real assembly");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectFolder))
            Directory.Delete(_projectFolder, recursive: true);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeConnector : OutProcReqnrollConnector
    {
        private readonly DiscoveryResult _result;

        public FakeConnector(DiscoveryResult result)
            : base(
                new DeveroomConfiguration(),
                Substitute.For<IIdeSupportLogger>(),
                TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
                AppContext.BaseDirectory,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(
                    TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
                NullTelemetryService.Instance)
        {
            _result = result;
        }

        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath) => _result;
        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }

    private IProjectScope MakeScope(string assemblyPath)
    {
        var scope = Substitute.For<IProjectScope>();
        scope.OutputAssemblyPath.Returns(assemblyPath);
        scope.ProjectName.Returns("MyApp.Tests");
        scope.ProjectFolder.Returns(_projectFolder);
        scope.TargetFrameworkMoniker.Returns(".NETCoreApp,Version=v8.0");
        return scope;
    }

    private void GivenConnectorReturns(DiscoveryResult result)
        => _factory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(result));

    private static DiscoveryResult SuccessfulResult() => new()
    {
        StepDefinitions =
        [
            new StepDefinition
            {
                Type           = "Given",
                Regex          = "^the first number is (.*)$",
                Method         = "MyApp.Steps.SetFirstNumber",
                ParamTypes     = "i",
                SourceLocation = "Steps.cs|10|5"
            }
        ],
        Hooks = []
    };

    private ConnectorDiscoveryService CreateSut() => new(_logger, _factory, new FileSystemForIDE());

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_builds_registry_from_connector_result()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.Should().NotBeSameAs(ProjectBindingRegistry.Invalid);
        registry.StepDefinitions.Should().HaveCount(1);
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RunDiscovery_invokes_the_factory_to_create_a_connector()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);

        CreateSut().RunDiscovery(scope, ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);

        _factory.Received(1).Create(scope);
    }

    // ── Hash guard ──────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_skips_connector_when_hash_unchanged()
    {
        GivenConnectorReturns(SuccessfulResult());
        var scope = MakeScope(_assemblyPath);
        var sut = CreateSut();

        // First run computes the current hash.
        var (firstRegistry, hash) = sut.RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);

        _factory.ClearReceivedCalls();

        // Second run with the same hash must short-circuit and return the prior registry.
        var (secondRegistry, secondHash) = sut.RunDiscovery(
            scope, firstRegistry, hash, CancellationToken.None);

        secondRegistry.Should().BeSameAs(firstRegistry);
        secondHash.Should().Be(hash);
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    // ── Resilience ───────────────────────────────────────────────────────────────

    [Fact]
    public void RunDiscovery_returns_last_good_when_output_assembly_path_is_empty()
    {
        var scope = MakeScope(string.Empty);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_output_assembly_missing_on_disk()
    {
        var scope = MakeScope(Path.Combine(_projectFolder, "does-not-exist.dll"));
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
        _factory.DidNotReceive().Create(Arg.Any<IProjectScope>());
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_connector_reports_failure()
    {
        GivenConnectorReturns(new DiscoveryResult { ErrorMessage = "boom" });
        var scope = MakeScope(_assemblyPath);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
    }

    [Fact]
    public void RunDiscovery_returns_last_good_when_connector_throws()
    {
        _factory.Create(Arg.Any<IProjectScope>()).Returns(new ThrowingConnector());
        var scope = MakeScope(_assemblyPath);
        var lastGood = SuccessfulRegistry();

        var (registry, hash) = CreateSut().RunDiscovery(
            scope, lastGood, lastHash: "prev", CancellationToken.None);

        registry.Should().BeSameAs(lastGood);
        hash.Should().Be("prev");
    }

    private sealed class ThrowingConnector : OutProcReqnrollConnector
    {
        public ThrowingConnector()
            : base(
                new DeveroomConfiguration(),
                Substitute.For<IIdeSupportLogger>(),
                TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0"),
                AppContext.BaseDirectory,
                ProcessorArchitectureSetting.UseSystem,
                DiscoveryTestSupport.MinimalProjectSettings(
                    TargetFrameworkMoniker.Create(".NETCoreApp,Version=v8.0")),
                NullTelemetryService.Instance)
        {
        }

        public override DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath)
            => throw new InvalidOperationException("connector blew up");

        protected override string GetConnectorPath(List<string> arguments) => "unused";
    }

    private ProjectBindingRegistry SuccessfulRegistry()
        => CreateSutWith(SuccessfulResult());

    private ProjectBindingRegistry CreateSutWith(DiscoveryResult result)
    {
        var localFactory = Substitute.For<IOutProcConnectorFactory>();
        localFactory.Create(Arg.Any<IProjectScope>()).Returns(new FakeConnector(result));
        var service = new ConnectorDiscoveryService(_logger, localFactory, new FileSystemForIDE());
        var (registry, _) = service.RunDiscovery(
            MakeScope(_assemblyPath), ProjectBindingRegistry.Invalid, string.Empty, CancellationToken.None);
        return registry;
    }

    // ── AttributeSourceLine backfill (#182 / #237) ─────────────────────────────

    private string WriteStepsFile(string content)
    {
        var path = Path.Combine(_projectFolder, "Steps.cs");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RunDiscovery_backfills_attribute_source_line_from_source_file()
    {
        var stepsFile = WriteStepsFile("""
            public class Steps
            {
                [Given("the first number is (.*)")]
                public void SetFirstNumber(int number) { }
            }
            """);
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = $"{stepsFile}|3|5"
                }
            ],
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().ContainSingle()
            .Which.AttributeSourceLine.Should().Be(3);
    }

    [Fact]
    public void RunDiscovery_resolves_source_file_via_index_reference()
    {
        var stepsFile = WriteStepsFile("""
            public class Steps
            {
                [Given("the first number is (.*)")]
                public void SetFirstNumber(int number) { }
            }
            """);
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = "#1|3|5"
                }
            ],
            SourceFiles = new Dictionary<string, string> { ["1"] = stepsFile },
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().ContainSingle()
            .Which.AttributeSourceLine.Should().Be(3);
    }

    [Fact]
    public void RunDiscovery_leaves_attribute_source_line_null_when_source_file_is_missing()
    {
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = Path.Combine(_projectFolder, "does-not-exist.cs") + "|3|5"
                }
            ],
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().ContainSingle()
            .Which.AttributeSourceLine.Should().BeNull();
    }

    [Fact]
    public void RunDiscovery_resolves_the_correct_line_for_each_attribute_on_a_shared_method()
    {
        var stepsFile = WriteStepsFile("""
            public class Steps
            {
                [Given("given text")]
                [When("when text")]
                public void MyStep() { }
            }
            """);
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition { Type = "Given", Regex = "given text", Method = "MyStep", SourceLocation = $"{stepsFile}|3" },
                new StepDefinition { Type = "When", Regex = "when text", Method = "MyStep", SourceLocation = $"{stepsFile}|4" }
            ],
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().HaveCount(2);
        registry.StepDefinitions.Single(sd => sd.StepDefinitionType == ScenarioBlock.Given)
            .AttributeSourceLine.Should().Be(3);
        registry.StepDefinitions.Single(sd => sd.StepDefinitionType == ScenarioBlock.When)
            .AttributeSourceLine.Should().Be(4);
    }

    [Fact]
    public void RunDiscovery_replaces_the_PDB_derived_line_with_the_method_identifiers_own_line()
    {
        // Regression guard (issue #471 follow-up): the connector's own wire-format SourceLocation
        // is a PDB sequence point, which can land a line or more into the method body rather than
        // on the declaration itself -- simulated here by pointing the wire location at line 5
        // (inside the body) even though the method identifier is actually on line 4. CodeLens (and
        // anything else anchored on SourceLocation.SourceFileLine) needs the AST-precise line, not
        // the raw wire one, once ConnectorDiscoveryService's backfill has a parsed AST available.
        var stepsFile = WriteStepsFile("""
            public class Steps
            {
                [Given("the first number is (.*)")]
                public void SetFirstNumber(int number)
                {
                    var x = 1;
                }
            }
            """);
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = $"{stepsFile}|5|9" // PDB sequence point: inside the body.
                }
            ],
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        // "    public void SetFirstNumber(int number)" is line 4 (1-based); the method identifier
        // starts at column 17.
        var binding = registry.StepDefinitions.Should().ContainSingle().Which;
        binding.Implementation.SourceLocation!.SourceFileLine.Should().Be(4);
        binding.Implementation.SourceLocation.SourceFileColumn.Should().Be(17);
    }

    [Fact]
    public void RunDiscovery_leaves_attribute_source_line_null_for_external_plugin_binding_with_no_local_source()
    {
        // Simulates a step definition contributed by a dynamically loaded / external Reqnroll
        // plugin assembly: the connector still resolves a PDB source location (via the #index
        // table), but that path is from the machine that built the plugin and does not exist on
        // this machine. Discovery as a whole must still succeed, just without the exact line.
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the plugin step$",
                    Method         = "PluginStep",
                    SourceLocation = "#1|10|5"
                }
            ],
            SourceFiles = new Dictionary<string, string> { ["1"] = @"C:\build-agent\plugin-repo\Steps.cs" },
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().ContainSingle()
            .Which.AttributeSourceLine.Should().BeNull();
    }

    [Fact]
    public void RunDiscovery_leaves_attribute_source_line_null_when_source_location_is_missing()
    {
        GivenConnectorReturns(new DiscoveryResult
        {
            StepDefinitions =
            [
                new StepDefinition
                {
                    Type           = "Given",
                    Regex          = "^the first number is (.*)$",
                    Method         = "SetFirstNumber",
                    ParamTypes     = "i",
                    SourceLocation = null
                }
            ],
            Hooks = []
        });
        var scope = MakeScope(_assemblyPath);

        var (registry, _) = CreateSut().RunDiscovery(
            scope, ProjectBindingRegistry.Invalid, lastHash: string.Empty, CancellationToken.None);

        registry.StepDefinitions.Should().ContainSingle()
            .Which.AttributeSourceLine.Should().BeNull();
    }
}
