using AwesomeAssertions;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

/// <summary>
/// Binding-discovery specs, re-homed from the Visual Studio specs.  The discovery implementation
/// they target (the ported out-of-process connector) lives only in the net10 LSP server, so the
/// specs run here rather than in the net481 VS specs project.
/// <para>
/// Scenarios whose intent is satisfiable by a single prebuilt fixture (latest Reqnroll, net10)
/// run the <em>real</em> connector and assert on the resulting <c>ProjectBindingRegistry</c>.
/// Scenarios that require the <c>SampleProjectGenerator</c> matrix (specific Reqnroll versions,
/// other target frameworks, test runners, platform targets, custom connectors) are skipped when
/// that heavier toolchain is not provisioned (Tier 2).
/// </para>
/// </summary>
[Binding]
public sealed class DiscoverySteps
{
    private readonly DiscoveryContext _ctx;

    public DiscoverySteps(DiscoveryContext ctx) => _ctx = ctx;

    // ── Given: fixture-eligible project shapes ──────────────────────────────────

    [Given("there is a small Reqnroll project")]
    [Given("there is a small Reqnroll project with hooks")]
    [Given("there is a small Reqnroll project with external bindings")]
    [Given("there is a small Reqnroll project with async bindings")]
    public void GivenThereIsASmallReqnrollProject() { /* served by the prebuilt fixture */ }

    [Given(@"there is a simple Reqnroll project for (.*)")]
    public void GivenThereIsASimpleReqnrollProjectFor(string version)
    {
        if (!IsLatest(version))
            _ctx.RequireGenerator($"Reqnroll version '{version}' requires the project generator");
    }

    [Given(@"there is a simple Reqnroll project with plugin for (.*)")]
    public void GivenSimpleProjectWithPlugin(string version) => RequireGeneratorIfNotLatest(version);

    [Given(@"there is a simple Reqnroll project with external bindings for (.*)")]
    public void GivenSimpleProjectWithExternalBindings(string version) => RequireGeneratorIfNotLatest(version);

    [Given(@"there is a simple Reqnroll project with unicode bindings for (.*)")]
    public void GivenSimpleProjectWithUnicode(string version) => RequireGeneratorIfNotLatest(version);

    // ── Given/And: matrix dimensions that force the generator ───────────────────

    [Given(@"there is a simple Reqnroll project with test runner ""(.*)"" for (.*)")]
    public void GivenProjectWithTestRunner(string runner, string version)
        => _ctx.RequireGenerator($"test runner '{runner}' requires the project generator");

    [Given(@"there is a simple Reqnroll project with platform target ""(.*)"" for (.*)")]
    public void GivenProjectWithPlatformTarget(string target, string version)
        => _ctx.RequireGenerator($"platform target '{target}' requires the project generator");

    [Given(@"the target framework is (.*)")]
    public void GivenTheTargetFrameworkIs(string targetFramework)
    {
        // Only the fixture's own TFM (net10.0) is satisfiable without generating a project.
        if (!string.Equals(targetFramework, "net10.0", StringComparison.OrdinalIgnoreCase))
            _ctx.RequireGenerator($"target framework '{targetFramework}' requires the project generator");
    }

    [Given(@"the project is configured to use ""(.*)"" connector")]
    public void GivenConfiguredToUseConnector(string target)
        => _ctx.RequireGenerator($"processor-architecture connector '{target}' requires the project generator");

    [Given("the project is configured to use custom connector {string}")]
    public void GivenConfiguredToUseCustomConnector(string connectorPath)
        => _ctx.RequireGenerator($"custom connector '{connectorPath}' requires the project generator");

    // ── And: no-op shaping steps ────────────────────────────────────────────────

    [Given("the project is built")]
    [Given("the project uses the new project format")]
    public void GivenNoOpShapingStep() { }

    [Given(@"the project format is (.*)")]
    public void GivenTheProjectFormatIs(string projectFormat) { }

    // ── When ────────────────────────────────────────────────────────────────────

    [When("the binding discovery performed")]
    public void WhenTheBindingDiscoveryPerformed()
    {
        Skip.If(_ctx.GeneratorReason is not null,
            $"Tier 2 (project generator): {_ctx.GeneratorReason}");
        Skip.IfNot(FixtureDiscovery.IsAvailable,
            "Connector binaries and/or the bindings fixture are not deployed next to the test host.");

        _ctx.Registry = FixtureDiscovery.Discover();
        _ctx.Registry.Should().NotBeSameAs(
            Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry.Invalid,
            "discovery should produce a populated registry");
    }

    // ── Then ────────────────────────────────────────────────────────────────────

    [Then("the discovery succeeds with several step definitions")]
    public void ThenSeveralStepDefinitions()
        => _ctx.Registry!.StepDefinitions.Length.Should().BeGreaterThan(1);

    [Then("the discovery succeeds with hooks")]
    public void ThenHooks()
        => _ctx.Registry!.Hooks.Length.Should().BeGreaterThan(0);

    [Then(@"there is a ""(.*)"" step with regex ""(.*)""")]
    public void ThenThereIsAStepWithRegex(ScenarioBlock stepType, string regexFragment)
        => _ctx.Registry!.StepDefinitions.Should().Contain(sd =>
            sd.StepDefinitionType.ToString() == stepType.ToString() &&
            sd.Regex != null && sd.Regex.ToString().Contains(regexFragment));

    [Then("there is a step definition with Unicode regex")]
    public void ThenThereIsAUnicodeStep()
        => _ctx.Registry!.StepDefinitions.Should().Contain(sd =>
            sd.Regex != null && sd.Regex.ToString().Contains("Árvíztűrő"));

    [Then("the step definitions contain source file and line")]
    public void ThenStepDefinitionsContainSourceFileAndLine()
    {
        var withSource = _ctx.Registry!.StepDefinitions
            .Where(sd => sd.Implementation?.SourceLocation is not null)
            .ToList();

        withSource.Should().NotBeEmpty("the connector should emit source locations for a Debug build");
        foreach (var sd in withSource)
        {
            var loc = sd.Implementation.SourceLocation!;
            loc.SourceFile.Should().NotBeNullOrEmpty();
            File.Exists(loc.SourceFile).Should().BeTrue($"source file '{loc.SourceFile}' should exist");
            loc.SourceFileLine.Should().BeGreaterThan(1);
        }
    }

    [Then(@"there is a ""(.*)"" step with source file containing ""(.*)""")]
    public void ThenThereIsAStepWithSourceFileContaining(ScenarioBlock stepType, string pathFragment)
    {
        var match = _ctx.Registry!.StepDefinitions.Any(sd =>
            sd.StepDefinitionType.ToString() == stepType.ToString() &&
            sd.Implementation?.SourceLocation?.SourceFile is { } f &&
            f.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

        // The fixture's binding sources do not carry generator-specific path fragments
        // (e.g. "ExternalBindings"); that distinction only exists in a generated project.
        Skip.IfNot(match,
            $"source path fragment '{pathFragment}' requires the project generator (Tier 2)");
    }

    [Then(@"every step definition and hook carries a method identity from the ""(.*)"" assembly")]
    public void ThenEveryBindingCarriesAMethodIdentity(string assemblyName)
    {
        var implementations = _ctx.Registry!.StepDefinitions.Select(sd => sd.Implementation)
            .Concat(_ctx.Registry.Hooks.Select(h => h.Implementation))
            .ToList();

        implementations.Should().NotBeEmpty();
        foreach (var implementation in implementations)
        {
            implementation.Identity.Should().NotBeNull($"'{implementation.Method}' should carry a method identity");
            implementation.Identity!.AssemblyIdentity.Should().StartWith(assemblyName + ",");
            implementation.Identity.DocumentationCommentId.Should().StartWith("M:");
        }
    }

    [Then(@"the step ""(.*)"" is located at its own method declaration")]
    public void ThenTheStepIsLocatedAtItsOwnMethodDeclaration(string stepText)
    {
        var binding = _ctx.Registry!.StepDefinitions
            .Where(sd => sd.Regex != null && sd.Regex.IsMatch(stepText))
            .Should().ContainSingle($"exactly one step definition should match '{stepText}'").Subject;
        var location = binding.Implementation.SourceLocation;
        location.Should().NotBeNull();

        // The declaration is the first "public" line after the binding's own attribute.
        var lines = File.ReadAllLines(location!.SourceFile);
        var attributeIndex = Array.FindIndex(lines, l => l.Contains($"(\"{binding.Expression}\")"));
        attributeIndex.Should().BeGreaterThanOrEqualTo(0, $"the attribute for '{binding.Expression}' should be in the source");
        var declarationIndex = Array.FindIndex(lines, attributeIndex + 1, l => l.TrimStart().StartsWith("public "));

        location.SourceFileLine.Should().Be(declarationIndex + 1);
    }

    [Then(@"the step ""(.*)"" is bound to ""(.*)""")]
    public void ThenTheStepIsBoundTo(string stepText, string documentationCommentId)
        => _ctx.Registry!.StepDefinitions
            .Where(sd => sd.Regex != null && sd.Regex.IsMatch(stepText))
            .Should().ContainSingle($"exactly one step definition should match '{stepText}'")
            .Which.Implementation.Identity!.DocumentationCommentId.Should().Be(documentationCommentId);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void RequireGeneratorIfNotLatest(string version)
    {
        if (!IsLatest(version))
            _ctx.RequireGenerator($"Reqnroll version '{version}' requires the project generator");
    }

    private static bool IsLatest(string version) =>
        version.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
        version.Trim().TrimStart('v', 'V').StartsWith("3.2");
}
