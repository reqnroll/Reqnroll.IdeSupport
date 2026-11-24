using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;

namespace ReqnrollConnector.AssemblyLoading.dotNET;

/// <summary>
/// Resolves assemblies from the shared frameworks the test assembly declares in its
/// <c>runtimeconfig.json</c> (for example <c>Microsoft.AspNetCore.App</c>), the way the .NET host
/// would if the test assembly were the app. The connector runs as its own app, whose runtimeconfig
/// only asks for <c>Microsoft.NETCore.App</c>, so without this an assembly that the test project
/// gets only from another shared framework (and that is therefore not in its output folder or its
/// <c>deps.json</c>) cannot be loaded.
/// </summary>
/// <remarks>
/// <para><c>Microsoft.NETCore.App</c> is skipped: the connector's own default load context already
/// provides it, and loading a second copy into the test assembly's context would break type identity.</para>
/// <para>Version selection follows the host's default roll-forward policy: within the declared major
/// version, the lowest minor version at or above the declared one, then its highest patch.
/// Prerelease directories are ignored.</para>
/// </remarks>
public sealed class SharedFrameworkAssemblyResolver : ICompilationAssemblyResolver
{
    private const string NetCoreAppFrameworkName = "Microsoft.NETCore.App";

    private readonly IReadOnlyList<SharedFrameworkReference> _frameworks;
    private readonly IReadOnlyList<string> _dotnetRoots;
    private readonly Dictionary<SharedFrameworkReference, string?> _selectedDirectories = new();

    public SharedFrameworkAssemblyResolver(
        IReadOnlyList<SharedFrameworkReference> frameworks,
        IReadOnlyList<string> dotnetRoots)
    {
        _frameworks = frameworks
            .Where(f => !string.Equals(f.Name, NetCoreAppFrameworkName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _dotnetRoots = dotnetRoots;
    }

    public bool TryResolveAssemblyPaths(CompilationLibrary library, List<string>? assemblies)
    {
        if (assemblies == null || _frameworks.Count == 0)
            return false;

        var assetPath = library.Assemblies?.FirstOrDefault();
        var assemblyFileName = string.IsNullOrEmpty(assetPath)
            ? library.Name + ".dll"
            : Path.GetFileName(assetPath);

        foreach (var framework in _frameworks)
        {
            var frameworkDirectory = GetFrameworkDirectory(framework);
            if (frameworkDirectory == null)
                continue;

            var candidate = Path.Combine(frameworkDirectory, assemblyFileName);
            if (File.Exists(candidate))
            {
                assemblies.Add(candidate);
                return true;
            }
        }

        return false;
    }

    private string? GetFrameworkDirectory(SharedFrameworkReference framework)
    {
        lock (_selectedDirectories)
        {
            if (!_selectedDirectories.TryGetValue(framework, out var directory))
            {
                directory = SelectFrameworkDirectory(framework, _dotnetRoots);
                _selectedDirectories[framework] = directory;
            }
            return directory;
        }
    }

    /// <summary>
    /// Returns the directory of the framework version the host would pick for
    /// <paramref name="framework"/> under the first dotnet root that has a matching version, or null.
    /// </summary>
    public static string? SelectFrameworkDirectory(SharedFrameworkReference framework, IEnumerable<string> dotnetRoots)
    {
        foreach (var root in dotnetRoots)
        {
            var frameworkRoot = Path.Combine(root, "shared", framework.Name);
            if (!Directory.Exists(frameworkRoot))
                continue;

            var candidates = Directory.GetDirectories(frameworkRoot)
                .Select(directory => (directory, version: ParseReleaseVersion(Path.GetFileName(directory))))
                .Where(c => c.version != null &&
                            c.version.Major == framework.Version.Major &&
                            c.version >= framework.Version)
                .Select(c => (c.directory, version: c.version!))
                .ToList();

            if (candidates.Count == 0)
                continue;

            var lowestMinor = candidates.Min(c => c.version.Minor);
            return candidates
                .Where(c => c.version.Minor == lowestMinor)
                .OrderByDescending(c => c.version)
                .First()
                .directory;
        }

        return null;
    }

    private static Version? ParseReleaseVersion(string directoryName)
    {
        if (directoryName.Contains('-'))
            return null;
        return RuntimeConfigReader.ParseFrameworkVersion(directoryName);
    }
}
