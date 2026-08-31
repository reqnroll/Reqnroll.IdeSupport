using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.FindUnusedStepDefinitions;

/// <summary>
/// Adapter-level tests for <see cref="FindUnusedStepDefinitionsHandler"/>: resolving registries,
/// mapping <see cref="UnusedStepDefinition"/> to the wire <see cref="UnusedStepDefinitionItem"/>
/// shape (incl. the 1-based → 0-based position conversion), and firing telemetry.
/// The scan/dedupe/match algorithm itself is covered by
/// <c>Reqnroll.IdeSupport.LSP.Core.Tests.FindUnusedStepDefinitions.FindUnusedStepDefinitionsServiceTests</c>.
/// </summary>
public class FindUnusedStepDefinitionsHandlerTests
{
    private readonly IProjectBindingRegistryLookup _registryLookup =
        Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IFindUnusedStepDefinitionsService _service =
        Substitute.For<IFindUnusedStepDefinitionsService>();

    private FindUnusedStepDefinitionsHandler CreateSut() =>
        new(_registryLookup, _service);

    private FindUnusedStepDefinitionsHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_registryLookup, _service, telemetry);

    private void SetupRegistries(
        params (string ProjectName, ProjectOwner Owner, ProjectBindingRegistry Registry)[] entries) =>
        _registryLookup.GetAllRegistries().Returns(entries.ToList());

    [Fact]
    public async Task HandleAsync_delegates_to_service_with_project_name_and_registry_only()
    {
        var owner    = new ProjectOwner("/ws/A.csproj", "net8.0");
        var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>());
        SetupRegistries(("A", owner, registry));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(Array.Empty<UnusedStepDefinition>());

        await CreateSut().HandleAsync(CancellationToken.None);

        _service.Received(1).FindUnusedStepDefinitions(
            Arg.Is<IReadOnlyList<(string ProjectName, ProjectBindingRegistry Registry)>>(
                r => r.Count == 1 && r[0].ProjectName == "A" && r[0].Registry == registry));
    }

    [Fact]
    public async Task HandleAsync_maps_service_result_fields_to_wire_item()
    {
        SetupRegistries(("A", new ProjectOwner("/ws/A.csproj", "net8.0"),
            ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>())));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(new[]
                {
                    new UnusedStepDefinition(
                        ProjectName: "MyProject",
                        ClassName: "StepDefs",
                        MethodName: "GivenSomething",
                        BindingExpression: "the sum is {int}",
                        SourceFile: "/ws/MySteps.cs",
                        SourceLine: 10,
                        SourceColumn: 5),
                });

        var result = await CreateSut().HandleAsync(CancellationToken.None);

        var item = result.Items.Single();
        item.ProjectName.Should().Be("MyProject");
        item.ClassName.Should().Be("StepDefs");
        item.MethodName.Should().Be("GivenSomething");
        item.BindingExpression.Should().Be("the sum is {int}");
        item.SourceFile.Should().Be("/ws/MySteps.cs");
        item.IsResolved.Should().BeTrue();
        item.RecordedSourceFile.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_maps_an_unresolved_source_path_onto_the_wire_item()
    {
        // The wire contract for issue #540: a binding whose source is not on this machine travels
        // with no sourceFile (so a client that only null-guards already stops navigating), plus
        // isResolved/recordedSourceFile so a client can explain it.
        SetupRegistries(("A", new ProjectOwner("/ws/A.csproj", "net8.0"),
            ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>())));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(new[]
                {
                    new UnusedStepDefinition(
                        ProjectName: "MyProject",
                        ClassName: "StepDefs",
                        MethodName: "GivenSomething",
                        BindingExpression: "the sum is {int}",
                        SourceFile: null,
                        SourceLine: 10,
                        SourceColumn: 5,
                        IsResolved: false,
                        RecordedSourceFile: "/workspaces/host-solution/Specs/Steps.cs"),
                });

        var result = await CreateSut().HandleAsync(CancellationToken.None);

        var item = result.Items.Single();
        item.SourceFile.Should().BeNull();
        item.IsResolved.Should().BeFalse();
        item.RecordedSourceFile.Should().Be("/workspaces/host-solution/Specs/Steps.cs");
        // The position still crosses the 1-based → 0-based boundary, unresolved or not.
        item.SourceLine.Should().Be(9);
        item.SourceChar.Should().Be(4);
    }

    [Fact]
    public async Task HandleAsync_converts_1based_source_line_to_0based()
    {
        SetupRegistries(("A", new ProjectOwner("/ws/A.csproj", "net8.0"),
            ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>())));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(new[]
                {
                    new UnusedStepDefinition("A", "C", "M", "x", "/ws/Steps.cs", SourceLine: 10, SourceColumn: 1),
                });

        var result = await CreateSut().HandleAsync(CancellationToken.None);

        result.Items.Single().SourceLine.Should().Be(9);  // 10 - 1
    }

    [Fact]
    public async Task HandleAsync_converts_1based_source_column_to_0based()
    {
        SetupRegistries(("A", new ProjectOwner("/ws/A.csproj", "net8.0"),
            ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>())));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(new[]
                {
                    new UnusedStepDefinition("A", "C", "M", "x", "/ws/Steps.cs", SourceLine: 1, SourceColumn: 5),
                });

        var result = await CreateSut().HandleAsync(CancellationToken.None);

        result.Items.Single().SourceChar.Should().Be(4);  // 5 - 1
    }

    [Fact]
    public async Task HandleAsync_no_projects_returns_empty_response()
    {
        SetupRegistries();
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(Array.Empty<UnusedStepDefinition>());

        var result = await CreateSut().HandleAsync(CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_emits_command_telemetry_with_counts()
    {
        SetupRegistries(("TestProject", new ProjectOwner("", ""),
            ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>())));
        _service.FindUnusedStepDefinitions(Arg.Any<IReadOnlyList<(string, ProjectBindingRegistry)>>())
                .Returns(new[]
                {
                    new UnusedStepDefinition("TestProject", "C", "M", "unused step", "/workspace/Steps.cs", 1, 1),
                });

        var telemetry = Substitute.For<ILspTelemetryService>();
        await CreateSutWithTelemetry(telemetry).HandleAsync(CancellationToken.None);

        telemetry.Received(1).SendEvent(
            "FindUnusedStepDefinitions command executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                1.Equals(d["UnusedStepDefinitions"]) &&
                1.Equals(d["ScannedFeatureFiles"]) &&
                false.Equals(d["IsCancellationRequested"])));
    }
}
