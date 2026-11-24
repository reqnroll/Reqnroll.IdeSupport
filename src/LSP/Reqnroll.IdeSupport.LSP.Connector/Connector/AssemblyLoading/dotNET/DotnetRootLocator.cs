namespace ReqnrollConnector.AssemblyLoading.dotNET;

/// <summary>
/// Finds the .NET installation folders whose <c>shared</c> directory may hold the test assembly's
/// shared frameworks.
/// </summary>
public static class DotnetRootLocator
{
    /// <summary>
    /// Returns, in order and without duplicates, the installation that is running the connector
    /// (the one the test assembly would normally run on too), <c>DOTNET_ROOT</c>, and the default
    /// Windows install location. Only existing folders are returned.
    /// </summary>
    public static IReadOnlyList<string> GetCandidateRoots()
    {
        var candidates = new[]
        {
            GetRunningInstallationRoot(),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            GetProgramFilesRoot()
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => Path.GetFullPath(path!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Derives the installation root from the core library's location, which in a framework-dependent
    /// app is <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>. Returns null for any
    /// other layout (for example a self-contained app).
    /// </summary>
    internal static string? GetRunningInstallationRoot()
    {
        var coreLibDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(coreLibDirectory))
            return null;

        var frameworkDirectory = Directory.GetParent(coreLibDirectory);
        var sharedDirectory = frameworkDirectory?.Parent;
        if (sharedDirectory == null ||
            !string.Equals(sharedDirectory.Name, "shared", StringComparison.OrdinalIgnoreCase))
            return null;

        return sharedDirectory.Parent?.FullName;
    }

    private static string? GetProgramFilesRoot()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        return string.IsNullOrEmpty(programFiles) ? null : Path.Combine(programFiles, "dotnet");
    }
}
