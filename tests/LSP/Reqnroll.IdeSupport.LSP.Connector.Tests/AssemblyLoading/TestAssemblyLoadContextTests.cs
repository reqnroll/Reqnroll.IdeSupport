using System.Reflection;
using ReqnrollConnector.AssemblyLoading.dotNET;
using ReqnrollConnector.Logging;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.AssemblyLoading;

/// <summary>
/// Regression coverage for <see cref="TestAssemblyLoadContext"/>'s constructor and its
/// <c>Load</c> override, pinning current resolve behaviour. Real cross-process test-assembly
/// discovery/dependency resolution can't be exercised in a unit test, so these load the test
/// assembly itself (which does have a real <c>.deps.json</c> alongside it) into a fresh context
/// to drive the constructor's dependency-context/RID/resolver setup for real.
/// </summary>
public class TestAssemblyLoadContextTests
{
    private readonly ILogger _log = Substitute.For<ILogger>();

    private static string SelfAssemblyPath => typeof(TestAssemblyLoadContextTests).Assembly.Location;

    private static MethodInfo LoadMethod =>
        typeof(TestAssemblyLoadContext).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void Constructor_loads_the_given_assembly_and_exposes_it_as_TestAssembly()
    {
        var sut = new TestAssemblyLoadContext(SelfAssemblyPath, (alc, path) => alc.LoadFromAssemblyPath(path), _log);

        sut.TestAssembly.Location.Should().Be(SelfAssemblyPath);
    }

    [Fact]
    public void Constructor_does_not_throw_while_deriving_the_target_framework_and_RID_fallbacks()
    {
        var act = () => new TestAssemblyLoadContext(SelfAssemblyPath, (alc, path) => alc.LoadFromAssemblyPath(path), _log);

        act.Should().NotThrow();
    }

    [Fact]
    public void Load_of_a_System_prefixed_assembly_returns_null_deferring_to_default_resolution()
    {
        var sut = new TestAssemblyLoadContext(SelfAssemblyPath, (alc, path) => alc.LoadFromAssemblyPath(path), _log);

        var result = LoadMethod.Invoke(sut, new object[] { new AssemblyName("System.SomeDefinitelyMissingLibrary") });

        result.Should().BeNull("System.* assemblies are deliberately left to the default resolution mechanism");
    }

    [Fact]
    public void Load_of_an_unresolvable_non_System_assembly_returns_null_without_throwing()
    {
        var sut = new TestAssemblyLoadContext(SelfAssemblyPath, (alc, path) => alc.LoadFromAssemblyPath(path), _log);

        object? result = null;
        var act = () => result = LoadMethod.Invoke(sut, new object[] { new AssemblyName("SomeDefinitelyMissingAssembly") });

        act.Should().NotThrow();
        result.Should().BeNull();
    }

    [Fact]
    public void Load_of_an_assembly_with_no_version_defaults_it_rather_than_throwing()
    {
        // AssemblyName without an explicit version leaves Version null; Load must default it to
        // 0.0 before building the requested-library lookup path rather than crashing on a null
        // Version dereference.
        var sut = new TestAssemblyLoadContext(SelfAssemblyPath, (alc, path) => alc.LoadFromAssemblyPath(path), _log);
        var assemblyName = new AssemblyName("SomeDefinitelyMissingAssembly");
        assemblyName.Version.Should().BeNull("precondition: no version specified");

        var act = () => LoadMethod.Invoke(sut, new object[] { assemblyName });

        act.Should().NotThrow();
    }
}
