using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>Locates checked-in source files from the test output directory.</summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> LazyRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reqnroll.IdeSupport.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Repo root (Reqnroll.IdeSupport.slnx) not found above " + AppContext.BaseDirectory);
    });

    public static string Root => LazyRoot.Value;

    public static string ExtensionProjectDir =>
        Path.Combine(Root, "src", "VisualStudio", "Reqnroll.IdeSupport.VisualStudio.Extension");

    public static string VssdkIntegrationProjectDir =>
        Path.Combine(Root, "src", "VisualStudio", "Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration");

    /// <summary>Checked-in <c>.cs</c> files of the Extension and VSSDKIntegration projects (no bin/obj).</summary>
    public static IEnumerable<string> VsExtensionSourceFiles() =>
        new[] { ExtensionProjectDir, VssdkIntegrationProjectDir }
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsUnder(path, "bin") && !IsUnder(path, "obj"));

    /// <summary>Path relative to the repo root, with forward slashes, for readable assertion messages.</summary>
    public static string Relative(string path) =>
        path.Substring(Root.Length).TrimStart('\\', '/').Replace('\\', '/');

    private static bool IsUnder(string path, string directoryName) =>
        path.IndexOf($"{Path.DirectorySeparatorChar}{directoryName}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0;
}
