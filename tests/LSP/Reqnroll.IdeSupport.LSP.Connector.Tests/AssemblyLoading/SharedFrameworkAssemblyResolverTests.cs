using Microsoft.Extensions.DependencyModel;
using ReqnrollConnector.AssemblyLoading.dotNET;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.AssemblyLoading;

/// <summary>
/// Covers how <see cref="SharedFrameworkAssemblyResolver"/> picks a shared framework version and
/// finds assemblies in it, against a fake dotnet root built in a temp folder.
/// </summary>
public sealed class SharedFrameworkAssemblyResolverTests : IDisposable
{
    private const string AspNetCore = "Microsoft.AspNetCore.App";
    private const string UserSecrets = "Microsoft.Extensions.Configuration.UserSecrets";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "reqnroll-sfr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string AddFrameworkAssembly(string framework, string version, string assemblyName, string? root = null)
    {
        var directory = Path.Combine(root ?? _root, "shared", framework, version);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static SharedFrameworkReference Framework(string name, string version) =>
        new(name, RuntimeConfigReader.ParseFrameworkVersion(version)!);

    private static CompilationLibrary RequestedLibrary(string name) =>
        new("package", name, "10.0.0.0", null, new[] { name + ".dll" }, Array.Empty<Dependency>(), true);

    private List<string> Resolve(SharedFrameworkAssemblyResolver sut, string name, out bool resolved)
    {
        var assemblies = new List<string>();
        resolved = sut.TryResolveAssemblyPaths(RequestedLibrary(name), assemblies);
        return assemblies;
    }

    [Fact]
    public void Resolves_an_assembly_from_a_declared_shared_framework()
    {
        var expected = AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        var assemblies = Resolve(sut, UserSecrets, out var resolved);

        resolved.Should().BeTrue();
        assemblies.Should().Equal(expected);
    }

    [Fact]
    public void Picks_the_highest_patch_by_version_not_by_string_order()
    {
        AddFrameworkAssembly(AspNetCore, "10.0.9", UserSecrets);
        var expected = AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        Resolve(sut, UserSecrets, out _).Should().Equal(expected);
    }

    [Fact]
    public void Rolls_forward_to_the_lowest_later_minor_but_never_to_another_major()
    {
        AddFrameworkAssembly(AspNetCore, "8.0.20", UserSecrets);
        var expected = AddFrameworkAssembly(AspNetCore, "9.1.2", UserSecrets);
        AddFrameworkAssembly(AspNetCore, "9.2.0", UserSecrets);
        AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "9.0.0") }, new[] { _root });

        Resolve(sut, UserSecrets, out _).Should().Equal(expected);
    }

    [Fact]
    public void Does_not_resolve_when_only_another_major_version_is_installed()
    {
        AddFrameworkAssembly(AspNetCore, "8.0.20", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        Resolve(sut, UserSecrets, out var resolved).Should().BeEmpty();
        resolved.Should().BeFalse();
    }

    [Fact]
    public void Ignores_prerelease_framework_directories()
    {
        AddFrameworkAssembly(AspNetCore, "10.0.0-rc.1.25451.107", UserSecrets);
        var expected = AddFrameworkAssembly(AspNetCore, "10.0.1", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        Resolve(sut, UserSecrets, out _).Should().Equal(expected);
    }

    [Fact]
    public void Does_not_fall_back_to_an_older_patch_when_the_selected_version_lacks_the_assembly()
    {
        // The host picks the framework version first; an assembly missing from that version is not
        // looked for in other versions.
        AddFrameworkAssembly(AspNetCore, "10.0.9", UserSecrets);
        AddFrameworkAssembly(AspNetCore, "10.0.15", "Some.Other.Assembly");
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        Resolve(sut, UserSecrets, out var resolved).Should().BeEmpty();
        resolved.Should().BeFalse();
    }

    [Fact]
    public void Never_resolves_from_Microsoft_NETCore_App()
    {
        AddFrameworkAssembly("Microsoft.NETCore.App", "10.0.15", "System.Text.Json");
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework("Microsoft.NETCore.App", "10.0.0") }, new[] { _root });

        Resolve(sut, "System.Text.Json", out var resolved).Should().BeEmpty();
        resolved.Should().BeFalse("the connector's default context already provides Microsoft.NETCore.App");
    }

    [Fact]
    public void Does_not_resolve_from_an_installed_framework_the_test_assembly_does_not_declare()
    {
        AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(Array.Empty<SharedFrameworkReference>(), new[] { _root });

        Resolve(sut, UserSecrets, out var resolved).Should().BeEmpty();
        resolved.Should().BeFalse();
    }

    [Fact]
    public void Uses_the_first_dotnet_root_that_has_a_matching_framework_version()
    {
        var emptyRoot = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyRoot);
        var secondRoot = Path.Combine(_root, "second");
        var expected = AddFrameworkAssembly(AspNetCore, "10.0.3", UserSecrets, secondRoot);
        var thirdRoot = Path.Combine(_root, "third");
        AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets, thirdRoot);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") },
            new[] { emptyRoot, secondRoot, thirdRoot });

        Resolve(sut, UserSecrets, out _).Should().Equal(expected);
    }

    [Fact]
    public void Returns_false_for_an_assembly_that_is_not_in_the_framework()
    {
        AddFrameworkAssembly(AspNetCore, "10.0.15", UserSecrets);
        var sut = new SharedFrameworkAssemblyResolver(new[] { Framework(AspNetCore, "10.0.0") }, new[] { _root });

        Resolve(sut, "SomeDefinitelyMissingAssembly", out var resolved).Should().BeEmpty();
        resolved.Should().BeFalse();
    }
}
